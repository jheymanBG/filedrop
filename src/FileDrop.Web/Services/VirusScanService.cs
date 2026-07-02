using FileDrop.Web.Models;

namespace FileDrop.Web.Services;

public interface IVirusScanService
{
    Task<ScanResult> ScanAsync(string path, CancellationToken cancellationToken);
    Task<string?> QuarantineAsync(string path, string originalFileName, Guid transferId, CancellationToken cancellationToken);
}

public sealed class VirusScanService : IVirusScanService
{
    private readonly IConfiguration _config;
    private readonly ILogger<VirusScanService> _logger;

    public VirusScanService(IConfiguration config, ILogger<VirusScanService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<ScanResult> ScanAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new ScanResult
            {
                Result = "Failed",
                Details = "File not found before scan."
            };
        }

        var defender = FindDefenderPath();

        if (string.IsNullOrWhiteSpace(defender))
        {
            return new ScanResult
            {
                Result = "Failed",
                Details = "Microsoft Defender command-line scanner MpCmdRun.exe was not found."
            };
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = defender,
            Arguments = $"-Scan -ScanType 3 -File \"{path}\" -DisableRemediation",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
        {
            return new ScanResult
            {
                Result = "Failed",
                Details = "Could not start Microsoft Defender scanner."
            };
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = (stdout + Environment.NewLine + stderr).Trim();

        _logger.LogInformation("Defender scan exit code {ExitCode} for {Path}", process.ExitCode, path);

        // Common MpCmdRun behavior: 0 = no malware detected.
        if (process.ExitCode == 0)
        {
            return new ScanResult
            {
                Result = "Clean",
                Details = combined
            };
        }

        var result = combined.Contains("threat", StringComparison.OrdinalIgnoreCase) ||
                     combined.Contains("malware", StringComparison.OrdinalIgnoreCase)
            ? "Infected"
            : "Failed";

        return new ScanResult
        {
            Result = result,
            ThreatName = TryExtractThreat(combined),
            Details = combined
        };
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

    private static string? FindDefenderPath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe"),
            @"C:\ProgramData\Microsoft\Windows Defender\Platform"
        };

        if (File.Exists(candidates[0]))
        {
            return candidates[0];
        }

        if (Directory.Exists(candidates[1]))
        {
            var newest = Directory.GetDirectories(candidates[1])
                .OrderByDescending(x => x)
                .Select(x => Path.Combine(x, "MpCmdRun.exe"))
                .FirstOrDefault(File.Exists);

            if (!string.IsNullOrWhiteSpace(newest))
            {
                return newest;
            }
        }

        return null;
    }

    private static string? TryExtractThreat(string text)
    {
        foreach (var line in text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("Threat", StringComparison.OrdinalIgnoreCase))
            {
                return line.Trim();
            }
        }

        return null;
    }
}
