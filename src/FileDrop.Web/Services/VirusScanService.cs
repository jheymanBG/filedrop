using FileDrop.Web.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FileDrop.Web.Services;

public interface IVirusScanService
{
    Task<ScanResult> ScanAsync(string path, CancellationToken cancellationToken);
    Task<string?> QuarantineAsync(string path, string originalFileName, Guid transferId, CancellationToken cancellationToken);
}

public sealed class VirusScanService : IVirusScanService
{
    private static readonly SemaphoreSlim DefenderScanGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, CachedScanResult> CleanCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CleanCacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(10);

    private readonly IConfiguration _config;
    private readonly ILogger<VirusScanService> _logger;
    private readonly ISettingsRepository _settings;

    public VirusScanService(IConfiguration config, ISettingsRepository settings, ILogger<VirusScanService> logger)
    {
        _config = config;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ScanResult> ScanAsync(string path, CancellationToken cancellationToken)
    {
        var configuredProvider = await _settings.GetStringAsync("Security.MalwareScanner.Provider", "AutoDetect");
        var provider = ResolveProvider(configuredProvider);

        if (provider.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return new ScanResult { Engine = "Disabled", Result = "Disabled", Details = "Malware scanning is disabled by administrator policy." };
        }

        if (provider.Equals("Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return new ScanResult
            {
                Engine = "AutoDetect",
                Result = "Unavailable",
                Details = "AutoDetect could not find an active SentinelOne agent or an active Microsoft Defender antivirus service."
            };
        }

        if (provider.Equals("SentinelOne", StringComparison.OrdinalIgnoreCase) || provider.Equals("ExternalAV", StringComparison.OrdinalIgnoreCase))
        {
            var sentinel = DetectSentinelOneHealth();
            if (provider.Equals("SentinelOne", StringComparison.OrdinalIgnoreCase) && !sentinel.IsHealthy)
            {
                return new ScanResult
                {
                    Engine = "SentinelOne Endpoint Protection",
                    Result = "Unavailable",
                    Details = $"SentinelOne was selected but FileDrop could not verify an active agent. {sentinel.Details}"
                };
            }

            return new ScanResult
            {
                Engine = sentinel.IsHealthy ? "SentinelOne Endpoint Protection" : "External Endpoint Protection",
                Result = "Protected",
                Details = sentinel.IsHealthy
                    ? $"SentinelOne endpoint protection is active. {sentinel.Details} FileDrop is relying on real-time endpoint protection; this does not claim that an explicit on-demand file scan was performed."
                    : "External AV mode selected. FileDrop is relying on host endpoint protection; no explicit on-demand scan was performed by FileDrop."
            };
        }

        if (!File.Exists(path))
        {
            return Failure("File not found before scan.");
        }

        var info = new FileInfo(path);
        var cacheKey = BuildCacheKey(info);
        if (CleanCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CleanCacheLifetime)
        {
            _logger.LogInformation("Using recent clean Defender result for {Path}; no duplicate scan required.", path);
            return new ScanResult
            {
                Result = "Clean",
                Details = "Microsoft Defender clean result reused for unchanged file from the previous scan in this upload session."
            };
        }

        var defender = FindDefenderExecutable();
        if (string.IsNullOrWhiteSpace(defender))
        {
            return Unavailable("Microsoft Defender command-line scanner MpCmdRun.exe was not found.", "Microsoft Defender");
        }

        await DefenderScanGate.WaitAsync(cancellationToken);
        try
        {
            // A second request may have completed while this request waited for the gate.
            if (CleanCache.TryGetValue(cacheKey, out cached) && DateTimeOffset.UtcNow - cached.CachedAt < CleanCacheLifetime)
            {
                _logger.LogInformation("Using recent clean Defender result for {Path} after waiting for scan gate.", path);
                return new ScanResult
                {
                    Result = "Clean",
                    Details = "Microsoft Defender clean result reused for unchanged file from the previous scan in this upload session."
                };
            }

            ScanAttemptResult? lastAttempt = null;

            // Exit code 2 is commonly seen when MpCmdRun cannot acquire its lock or otherwise
            // cannot complete a command. Serialize scans and retry transient command failures.
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastAttempt = await RunDefenderAsync(defender, path, cancellationToken);

                _logger.LogInformation(
                    "Defender scan attempt {Attempt}/3 exit code {ExitCode} for {Path}",
                    attempt,
                    lastAttempt.ExitCode,
                    path);

                if (lastAttempt.ExitCode == 0 || IsThreatDetected(lastAttempt.Output))
                {
                    break;
                }

                if (attempt < 3 && IsRetryableCommandFailure(lastAttempt.ExitCode, lastAttempt.Output))
                {
                    var delay = attempt == 1 ? TimeSpan.FromMilliseconds(750) : TimeSpan.FromMilliseconds(1500);
                    _logger.LogWarning(
                        "Defender command did not complete successfully for {Path}. ExitCode={ExitCode}. Retrying after {DelayMs} ms. Output={Output}",
                        path,
                        lastAttempt.ExitCode,
                        (int)delay.TotalMilliseconds,
                        SanitizeForLog(lastAttempt.Output));
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                break;
            }

            if (lastAttempt is null)
            {
                return Failure("Microsoft Defender scan did not start.");
            }

            var output = lastAttempt.Output;

            if (lastAttempt.ExitCode == 0 && !IsThreatDetected(output))
            {
                CleanCache[cacheKey] = new CachedScanResult(DateTimeOffset.UtcNow);
                TrimExpiredCache();

                return new ScanResult
                {
                    Result = "Clean",
                    Details = SafeDetails(output, "Microsoft Defender completed the scan and found no threats.")
                };
            }

            if (IsThreatDetected(output))
            {
                return new ScanResult
                {
                    Result = "Infected",
                    ThreatName = TryExtractThreat(output),
                    Details = SafeDetails(output, "Microsoft Defender reported a threat in the uploaded file.")
                };
            }

            _logger.LogError(
                "Microsoft Defender scanner error for {Path}. ExitCode={ExitCode}. Output={Output}",
                path,
                lastAttempt.ExitCode,
                SanitizeForLog(output));

            return Unavailable(
                $"Microsoft Defender could not complete the scan (exit code {lastAttempt.ExitCode}). " +
                SafeDetails(output, "No malware detection was reported; this is a scanner/command failure."),
                "Microsoft Defender");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Microsoft Defender scan timed out for {Path}.", path);
            return Unavailable("Microsoft Defender scan timed out before a result was returned.", "Microsoft Defender");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Microsoft Defender scan execution failed for {Path}.", path);
            return Unavailable($"Microsoft Defender scan execution failed: {ex.Message}", "Microsoft Defender");
        }
        finally
        {
            DefenderScanGate.Release();
        }
    }

    private async Task<ScanAttemptResult> RunDefenderAsync(string defender, string path, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = defender,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // ArgumentList avoids quoting/escaping problems with spaces and punctuation in file names.
        psi.ArgumentList.Add("-Scan");
        psi.ArgumentList.Add("-ScanType");
        psi.ArgumentList.Add("3");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("-DisableRemediation");

        using var process = Process.Start(psi);
        if (process is null)
        {
            return new ScanAttemptResult(-1, "Could not start Microsoft Defender scanner.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ScanTimeout);

        await process.WaitForExitAsync(timeoutCts.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = string.Join(Environment.NewLine, new[] { stdout, stderr }
            .Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

        return new ScanAttemptResult(process.ExitCode, combined);
    }

    private static bool IsRetryableCommandFailure(int exitCode, string output)
    {
        if (exitCode == 2) return true;

        return output.Contains("0x80070020", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("sharing violation", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("lock", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("failed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsThreatDetected(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;

        // Explicit clean statements must never be interpreted as an infection merely because
        // they contain the word "threat".
        if (Regex.IsMatch(output, @"found\s+no\s+threats?", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(output, @"detected\s+0\s+threats?", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(output, @"has\s+detected\s+0\s+threats?", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return Regex.IsMatch(output, @"(?:detected|found|has\s+detected)\s+[1-9]\d*\s+threats?", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(output, @"^\s*Threat\s*(?:Name)?\s*[:=]", RegexOptions.IgnoreCase | RegexOptions.Multiline) ||
               output.Contains("malware detected", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("threat detected", StringComparison.OrdinalIgnoreCase);
    }

    private static ScanResult Failure(string details) => new()
    {
        Result = "Failed",
        Details = details
    };

    private static ScanResult Unavailable(string details, string engine) => new()
    {
        Engine = engine,
        Result = "Unavailable",
        Details = details
    };

    private static string BuildCacheKey(FileInfo info) =>
        $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

    private static void TrimExpiredCache()
    {
        var cutoff = DateTimeOffset.UtcNow - CleanCacheLifetime;
        foreach (var entry in CleanCache)
        {
            if (entry.Value.CachedAt < cutoff)
            {
                CleanCache.TryRemove(entry.Key, out _);
            }
        }
    }

    private static string SafeDetails(string output, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(output) ? fallback : output.Trim();
        return value.Length <= 4000 ? value : value[..4000] + " ...[truncated]";
    }

    private static string SanitizeForLog(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "(no output)";
        var value = output.Replace("\r", " ").Replace("\n", " | ").Trim();
        return value.Length <= 2000 ? value : value[..2000] + " ...[truncated]";
    }

    public async Task<string?> QuarantineAsync(string path, string originalFileName, Guid transferId, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var quarantineRoot = _config["Storage:QuarantinePath"] ?? "C:\\SecureFileTransfer\\Quarantine";
        var folder = Path.Combine(quarantineRoot, DateTime.Now.ToString("yyyy"), DateTime.Now.ToString("MM"), transferId.ToString("N"));
        Directory.CreateDirectory(folder);

        var safeName = Path.GetFileName(originalFileName);
        var quarantinePath = Path.Combine(folder, $"{Guid.NewGuid():N}-{safeName}.quarantine");

        await using (var input = File.OpenRead(path))
        await using (var output = File.Create(quarantinePath))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Leave original if deletion fails.
        }

        return quarantinePath;
    }

    private string ResolveProvider(string configured) => ResolveProviderName(configured);

    public static string ResolveProviderName(string configured)
    {
        if (!configured.Equals("AutoDetect", StringComparison.OrdinalIgnoreCase)) return configured;

        var sentinel = DetectSentinelOneHealth();
        if (sentinel.IsHealthy) return "SentinelOne";

        var defender = DetectDefenderHealth();
        if (defender.IsHealthy) return "MicrosoftDefender";

        return "Unavailable";
    }

    public static SentinelOneHealth DetectSentinelOneHealth()
    {
        var installed = false;
        var serviceRunning = false;
        var processRunning = false;
        var cliAvailable = false;
        var cliHealthy = false;
        var details = new List<string>();

        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SentinelOne"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SentinelOne")
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        try { installed = roots.Any(Directory.Exists); } catch { }

        // Installation can be verified from the service registry key even when the IIS
        // identity cannot query all SentinelOne CLI/service details.
        try
        {
            using var serviceKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SentinelAgent");
            installed = installed || serviceKey is not null;
        }
        catch (Exception ex)
        {
            details.Add($"Service registry query unavailable: {ex.Message}");
        }

        // Query Service Control Manager. A permissions failure must not prevent the
        // independent process check below.
        var sc = RunProcessForHealth("sc.exe", new[] { "query", "SentinelAgent" }, TimeSpan.FromSeconds(5));
        if (sc.Started && sc.ExitCode == 0)
        {
            installed = true;
            serviceRunning = Regex.IsMatch(sc.Output, @"STATE\s*:\s*4\s+RUNNING", RegexOptions.IgnoreCase);
            details.Add(serviceRunning ? "SentinelAgent service reports RUNNING." : "SentinelAgent service exists but is not RUNNING.");
        }
        else if (sc.Started)
        {
            details.Add($"SentinelAgent service query returned exit code {sc.ExitCode}.");
        }
        else
        {
            details.Add("SentinelAgent service query could not be started.");
        }

        // Always perform a process check. Phase 65b only did this when sc.exe could not
        // start, which caused false negatives under restricted IIS identities.
        try
        {
            processRunning = Process.GetProcessesByName("SentinelAgent").Length > 0;
            if (processRunning)
            {
                installed = true;
                details.Add("SentinelAgent process is running.");
            }
        }
        catch (Exception ex)
        {
            details.Add($"SentinelAgent process query unavailable: {ex.Message}");
        }

        string? sentinelCtl = null;
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                sentinelCtl = Directory.EnumerateFiles(root, "sentinelctl.exe", SearchOption.AllDirectories)
                    .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(sentinelCtl)) break;
        }

        cliAvailable = !string.IsNullOrWhiteSpace(sentinelCtl) && File.Exists(sentinelCtl);
        if (cliAvailable && sentinelCtl is not null)
        {
            var ctl = RunProcessForHealth(sentinelCtl, new[] { "status" }, TimeSpan.FromSeconds(8));
            var lower = ctl.Output.ToLowerInvariant();
            cliHealthy = ctl.Started && ctl.ExitCode == 0 &&
                !lower.Contains("not running") &&
                !lower.Contains("disabled") &&
                !lower.Contains("inactive") &&
                !lower.Contains("error");
            details.Add(cliHealthy ? "sentinelctl status completed successfully." : "sentinelctl status was unavailable or did not report a healthy state.");
        }
        else
        {
            details.Add("sentinelctl was not found; Windows service/process state is used.");
        }

        // Any strong local running signal is sufficient. CLI is supplementary and may
        // be restricted by SentinelOne self-protection or the IIS app-pool identity.
        var healthy = installed && (serviceRunning || processRunning || cliHealthy);
        var status = healthy ? "Active" : installed ? "Installed but active state could not be verified" : "Not detected";
        return new SentinelOneHealth(installed, serviceRunning, processRunning, cliAvailable, cliHealthy, healthy, status, string.Join(" ", details));
    }

    public static DefenderHealth DetectDefenderHealth()
    {
        var executableAvailable = !string.IsNullOrWhiteSpace(FindDefenderExecutable());
        var serviceRunning = false;
        var details = new List<string>();

        var sc = RunProcessForHealth("sc.exe", new[] { "query", "WinDefend" }, TimeSpan.FromSeconds(5));
        if (sc.Started && sc.ExitCode == 0)
        {
            serviceRunning = Regex.IsMatch(sc.Output, @"STATE\s*:\s*4\s+RUNNING", RegexOptions.IgnoreCase);
            details.Add(serviceRunning ? "WinDefend service reports RUNNING." : "WinDefend service is not RUNNING.");
        }
        else
        {
            details.Add("WinDefend service could not be verified as running.");
        }

        var healthy = executableAvailable && serviceRunning;
        return new DefenderHealth(executableAvailable, serviceRunning, healthy, healthy ? "Active" : "Unavailable", string.Join(" ", details));
    }

    public static bool IsSentinelOneDetected() => DetectSentinelOneHealth().IsHealthy;

    private static HealthProcessResult RunProcessForHealth(string fileName, IEnumerable<string> args, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) return new HealthProcessResult(false, -1, "Could not start process.");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new HealthProcessResult(true, -2, "Health command timed out.");
            }

            Task.WaitAll(outputTask, errorTask);
            var output = string.Join(Environment.NewLine, new[] { outputTask.Result, errorTask.Result }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            return new HealthProcessResult(true, process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return new HealthProcessResult(false, -1, ex.Message);
        }
    }

    public static string? FindDefenderExecutable()
    {
        // Prefer the current Defender platform binary. The Program Files copy can be an older
        // compatibility launcher on current Windows builds.
        const string platformRoot = @"C:\ProgramData\Microsoft\Windows Defender\Platform";
        if (Directory.Exists(platformRoot))
        {
            var newest = Directory.GetDirectories(platformRoot)
                .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => Path.Combine(x, "MpCmdRun.exe"))
                .FirstOrDefault(File.Exists);

            if (!string.IsNullOrWhiteSpace(newest))
            {
                return newest;
            }
        }

        var programFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Windows Defender",
            "MpCmdRun.exe");

        return File.Exists(programFiles) ? programFiles : null;
    }

    private static string? TryExtractThreat(string text)
    {
        foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Regex.IsMatch(line, @"^\s*Threat\s*(?:Name)?\s*[:=]", RegexOptions.IgnoreCase))
            {
                return line.Trim();
            }
        }

        return null;
    }

    public sealed record SentinelOneHealth(
        bool Installed,
        bool ServiceRunning,
        bool ProcessRunning,
        bool CliAvailable,
        bool CliHealthy,
        bool IsHealthy,
        string Status,
        string Details);

    public sealed record DefenderHealth(
        bool ExecutableAvailable,
        bool ServiceRunning,
        bool IsHealthy,
        string Status,
        string Details);

    private sealed record HealthProcessResult(bool Started, int ExitCode, string Output);
    private sealed record ScanAttemptResult(int ExitCode, string Output);
    private sealed record CachedScanResult(DateTimeOffset CachedAt);
}
