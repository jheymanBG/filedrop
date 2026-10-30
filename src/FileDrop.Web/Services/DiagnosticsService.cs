using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IDiagnosticsService
{
    Task<SystemDiagnosticsViewModel> RunAsync(CancellationToken cancellationToken = default);
    Task<byte[]> CreateSupportPackageAsync(CancellationToken cancellationToken = default);
}

public sealed class DiagnosticsService : IDiagnosticsService
{
    private static readonly DateTimeOffset ProcessStartedAt = new(Process.GetCurrentProcess().StartTime);
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _environment;
    private readonly IStorageProtectionService _storageProtection;
    private readonly ISettingsRepository _settings;

    public DiagnosticsService(IConfiguration config, IWebHostEnvironment environment, IStorageProtectionService storageProtection, ISettingsRepository settings)
    {
        _config = config;
        _environment = environment;
        _storageProtection = storageProtection;
        _settings = settings;
    }

    public async Task<SystemDiagnosticsViewModel> RunAsync(CancellationToken cancellationToken = default)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "Unknown";
        var commit = _config["Build:CommitHash"] ?? Environment.GetEnvironmentVariable("FILEDROP_COMMIT") ?? "Not recorded";
        var buildTimestamp = _config["Build:Timestamp"] ?? File.GetLastWriteTimeUtc(assembly.Location).ToString("O");

        var model = new SystemDiagnosticsViewModel
        {
            ProductVersion = version,
            CommitHash = commit,
            BuildTimestamp = buildTimestamp,
            EnvironmentName = _environment.EnvironmentName,
            MachineName = Environment.MachineName,
            OperatingSystem = RuntimeInformation.OSDescription,
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            StartedAt = ProcessStartedAt,
            Uptime = DateTimeOffset.Now - ProcessStartedAt,
            CheckedAt = DateTimeOffset.Now
        };

        model.Checks.Add(await CheckSqlAsync(model, cancellationToken));
        model.Checks.Add(await CheckStorageAsync(cancellationToken));
        model.Checks.Add(await CheckSmtpAsync(cancellationToken));
        model.Checks.Add(CheckEntra());
        model.Checks.Add(await CheckEndpointProtectionAsync());
        model.Checks.Add(CheckLogs());
        return model;
    }

    public async Task<byte[]> CreateSupportPackageAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = await RunAsync(cancellationToken);
        await using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "diagnostics.json", JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true }));
            WriteEntry(archive, "configuration-summary.json", JsonSerializer.Serialize(BuildSanitizedConfiguration(), new JsonSerializerOptions { WriteIndented = true }));
            WriteEntry(archive, "environment.txt", BuildEnvironmentText(diagnostics));
            AddRecentLogs(archive);
        }
        return memory.ToArray();
    }

    private async Task<DiagnosticCheckResult> CheckSqlAsync(SystemDiagnosticsViewModel model, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var cs = _config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection is missing.");
            await using var db = new SqlConnection(cs);
            await db.OpenAsync(cancellationToken);
            await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));
            model.DatabaseSchemaVersion = await ReadSchemaVersionAsync(db, cancellationToken);
            return Result("SQL Server", true, "Healthy", $"Connected. Schema: {model.DatabaseSchemaVersion}", sw);
        }
        catch (Exception ex)
        {
            return Result("SQL Server", false, "Failed", ex.Message, sw);
        }
    }

    private async Task<string> ReadSchemaVersionAsync(SqlConnection db, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID('dbo.SchemaVersions','U') IS NOT NULL
                SELECT TOP 1 Version FROM dbo.SchemaVersions ORDER BY AppliedDate DESC;
            ELSE
                SELECT CAST('Legacy / untracked' AS nvarchar(100));
            """;
        return await db.ExecuteScalarAsync<string>(new CommandDefinition(sql, cancellationToken: cancellationToken)) ?? "Unknown";
    }

    private async Task<DiagnosticCheckResult> CheckStorageAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var root = await _storageProtection.GetActiveRootPathAsync();
            Directory.CreateDirectory(root);
            var test = Path.Combine(root, $".filedrop-health-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(test, "FileDrop health test", cancellationToken);
            await File.ReadAllTextAsync(test, cancellationToken);
            File.Delete(test);
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(root)) ?? root;
            var drive = new DriveInfo(driveRoot);
            return Result("Active Storage", true, "Healthy", $"{root}; free {FormatBytes(drive.AvailableFreeSpace)} of {FormatBytes(drive.TotalSize)}", sw);
        }
        catch (Exception ex)
        {
            return Result("Active Storage", false, "Failed", ex.Message, sw);
        }
    }

    private async Task<DiagnosticCheckResult> CheckSmtpAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var host = _config["Email:SmtpServer"];
        var port = _config.GetValue<int?>("Email:SmtpPort") ?? 25;
        if (string.IsNullOrWhiteSpace(host)) return Result("SMTP", false, "Not configured", "Email:SmtpServer is empty.", sw);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);
            return Result("SMTP", true, "Reachable", $"TCP connection succeeded to {host}:{port}.", sw);
        }
        catch (Exception ex)
        {
            return Result("SMTP", false, "Unreachable", ex.Message, sw);
        }
    }

    private DiagnosticCheckResult CheckEntra()
    {
        var sw = Stopwatch.StartNew();
        var tenant = _config["AzureAd:TenantId"];
        var client = _config["AzureAd:ClientId"];
        var callback = _config["AzureAd:CallbackPath"];
        var ok = Guid.TryParse(tenant, out _) && Guid.TryParse(client, out _) && !string.IsNullOrWhiteSpace(callback);
        return Result("Microsoft Entra", ok, ok ? "Configured" : "Incomplete", $"TenantId={(string.IsNullOrWhiteSpace(tenant) ? "missing" : "set")}; ClientId={(string.IsNullOrWhiteSpace(client) ? "missing" : "set")}; Callback={callback ?? "missing"}", sw);
    }

    private async Task<DiagnosticCheckResult> CheckEndpointProtectionAsync()
    {
        var sw = Stopwatch.StartNew();
        var enabled = await _settings.GetBoolAsync("Security.EnableVirusScanning", false);
        if (!enabled)
            return Result("Endpoint Protection", true, "Disabled by policy", "Security.EnableVirusScanning=false", sw);

        var configured = await _settings.GetStringAsync("Security.MalwareScanner.Provider", "AutoDetect");
        var effective = VirusScanService.ResolveProviderName(configured);

        if (effective.Equals("SentinelOne", StringComparison.OrdinalIgnoreCase))
        {
            var s1 = VirusScanService.DetectSentinelOneHealth();
            return Result("Endpoint Protection", s1.IsHealthy, s1.IsHealthy ? "SentinelOne active" : "SentinelOne unavailable",
                $"Configured={configured}; Effective={effective}; {s1.Details}", sw);
        }

        if (effective.Equals("MicrosoftDefender", StringComparison.OrdinalIgnoreCase))
        {
            var defender = VirusScanService.DetectDefenderHealth();
            return Result("Endpoint Protection", defender.IsHealthy, defender.IsHealthy ? "Microsoft Defender active" : "Microsoft Defender unavailable",
                $"Configured={configured}; Effective={effective}; {defender.Details}", sw);
        }

        if (effective.Equals("ExternalAV", StringComparison.OrdinalIgnoreCase))
            return Result("Endpoint Protection", true, "External AV declared", "FileDrop is configured to rely on external endpoint protection; no synchronous on-demand verdict is claimed.", sw);

        if (effective.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            return Result("Endpoint Protection", true, "Provider disabled", "Malware provider is explicitly Disabled.", sw);

        return Result("Endpoint Protection", false, "No active provider",
            $"Configured={configured}; AutoDetect could not verify SentinelOne or an active Microsoft Defender service.", sw);
    }

    private DiagnosticCheckResult CheckLogs()
    {
        var sw = Stopwatch.StartNew();
        var path = _config["Storage:LogsPath"] ?? "C:\\SecureFileTransfer\\Logs";
        try
        {
            Directory.CreateDirectory(path);
            return Result("Application Logs", true, "Available", path, sw);
        }
        catch (Exception ex)
        {
            return Result("Application Logs", false, "Unavailable", ex.Message, sw);
        }
    }

    private Dictionary<string, string?> BuildSanitizedConfiguration()
    {
        var result = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _config.AsEnumerable())
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            result[pair.Key] = IsSensitive(pair.Key) ? "***REDACTED***" : pair.Value;
        }
        return new Dictionary<string, string?>(result);
    }

    private static bool IsSensitive(string key)
    {
        var value = key.ToLowerInvariant();
        return value.Contains("password") || value.Contains("secret") || value.Contains("token") || value.Contains("connectionstring") || value.Contains("apikey") || value.Contains("privatekey");
    }

    private void AddRecentLogs(ZipArchive archive)
    {
        var logPath = _config["Storage:LogsPath"] ?? "C:\\SecureFileTransfer\\Logs";
        if (!Directory.Exists(logPath)) return;
        foreach (var file in Directory.EnumerateFiles(logPath, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
        {
            try
            {
                var info = new FileInfo(file);
                var bytesToRead = (int)Math.Min(info.Length, 256 * 1024);
                using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(-bytesToRead, SeekOrigin.End);
                var buffer = new byte[bytesToRead];
                _ = stream.Read(buffer, 0, buffer.Length);
                WriteEntry(archive, $"logs/{Path.GetFileName(file)}", Encoding.UTF8.GetString(buffer));
            }
            catch { }
        }
    }

    private static string BuildEnvironmentText(SystemDiagnosticsViewModel model) => $"""
        FileDrop support package
        Generated: {model.CheckedAt:O}
        Version: {model.ProductVersion}
        Commit: {model.CommitHash}
        Build: {model.BuildTimestamp}
        Environment: {model.EnvironmentName}
        Machine: {model.MachineName}
        OS: {model.OperatingSystem}
        Runtime: {model.RuntimeVersion}
        Uptime: {model.Uptime}
        Schema: {model.DatabaseSchemaVersion}
        """;

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static DiagnosticCheckResult Result(string name, bool healthy, string status, string details, Stopwatch sw)
    {
        sw.Stop();
        return new DiagnosticCheckResult { Name = name, IsHealthy = healthy, Status = status, Details = details, Duration = sw.Elapsed };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
