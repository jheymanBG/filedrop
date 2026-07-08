using System.Xml.Linq;
using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IUploadManagerService
{
    Task<UploadManagerViewModel> GetAsync();
    Task<UploadManagerDashboard> GetDashboardAsync();
    Task SaveAsync(decimal maximumUploadGb, int maximumFilesPerTransfer, int requestTimeoutMinutes);
    Task SaveRetentionAsync(int retentionDays, bool automaticCleanupEnabled, int automaticCleanupHourUtc);
    Task RepairAsync();
}

public sealed class UploadManagerService : IUploadManagerService
{
    private readonly ISettingsRepository _settings;
    private readonly IConfiguration _config;
    private readonly string _connectionString;

    public UploadManagerService(ISettingsRepository settings, IConfiguration config)
    {
        _settings = settings;
        _config = config;
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task<UploadManagerViewModel> GetAsync()
    {
        var bytes = await GetConfiguredBytesAsync();
        var webConfig = GetPublishWebConfigPath();
        var cleanupEnabled = await _settings.GetIntAsync("Retention.AutomaticCleanupEnabled", 0) == 1;

        return new UploadManagerViewModel
        {
            MaximumUploadBytes = bytes,
            MaximumUploadGB = Math.Round(bytes / 1024m / 1024m / 1024m, 2),
            MaximumFilesPerTransfer = await _settings.GetIntAsync("Upload.MaximumFilesPerTransfer", 500),
            RequestTimeoutMinutes = await _settings.GetIntAsync("Upload.RequestTimeoutMinutes", 1440),
            RetentionDays = await _settings.GetIntAsync("Retention.Days", 30),
            AutomaticCleanupEnabled = cleanupEnabled,
            AutomaticCleanupHourUtc = await _settings.GetIntAsync("Retention.AutomaticCleanupHourUtc", 3),
            PublishWebConfigPath = webConfig,
            PublishWebConfigExists = File.Exists(webConfig),
            EffectiveWebConfigBytes = File.Exists(webConfig) ? ReadWebConfigLimit(webConfig) : 0,
            Dashboard = await GetDashboardAsync()
        };
    }

    public async Task<UploadManagerDashboard> GetDashboardAsync()
    {
        await using var db = new SqlConnection(_connectionString);
        var dashboard = new UploadManagerDashboard();

        var chunkTableExists = await db.ExecuteScalarAsync<int>("SELECT CASE WHEN OBJECT_ID('dbo.ChunkedUploadSessions', 'U') IS NULL THEN 0 ELSE 1 END");
        if (chunkTableExists == 1)
        {
            dashboard = await db.QuerySingleAsync<UploadManagerDashboard>("""
                SELECT
                    ActiveUploadCount = SUM(CASE WHEN Status = 'Uploading' THEN 1 ELSE 0 END),
                    FinalizingUploadCount = SUM(CASE WHEN Status = 'Finalizing' THEN 1 ELSE 0 END),
                    CompletedUploadCount24h = SUM(CASE WHEN Status = 'Complete' AND CompletedDate >= DATEADD(day, -1, SYSUTCDATETIME()) THEN 1 ELSE 0 END),
                    FailedUploadCount24h = SUM(CASE WHEN Status = 'Failed' AND LastActivityDate >= DATEADD(day, -1, SYSUTCDATETIME()) THEN 1 ELSE 0 END),
                    AbandonedUploadCount = SUM(CASE WHEN Status IN ('Uploading','Finalizing') AND LastActivityDate < DATEADD(hour, -24, SYSUTCDATETIME()) THEN 1 ELSE 0 END),
                    ActiveBytesReceived = COALESCE(SUM(CASE WHEN Status IN ('Uploading','Finalizing') THEN BytesReceived ELSE 0 END), 0),
                    ActiveTotalBytes = COALESCE(SUM(CASE WHEN Status IN ('Uploading','Finalizing') THEN TotalBytes ELSE 0 END), 0),
                    CompletedBytes24h = COALESCE(SUM(CASE WHEN Status = 'Complete' AND CompletedDate >= DATEADD(day, -1, SYSUTCDATETIME()) THEN TotalBytes ELSE 0 END), 0),
                    OldestActiveDate = MIN(CASE WHEN Status IN ('Uploading','Finalizing') THEN CreatedDate END),
                    LastCompletedDate = MAX(CASE WHEN Status = 'Complete' THEN CompletedDate END)
                FROM dbo.ChunkedUploadSessions;
                """);

            dashboard.ActiveUploads = (await db.QueryAsync<UploadManagerActiveUploadRow>("""
                SELECT TOP 25 UploadId, OriginalFileName, CreatedByEmail, Status, ChunksReceived, TotalChunks,
                       BytesReceived, TotalBytes, CreatedDate, LastActivityDate
                FROM dbo.ChunkedUploadSessions
                WHERE Status IN ('Uploading','Finalizing')
                ORDER BY LastActivityDate DESC;
                """)).ToList();

            dashboard.RecentUploads = (await db.QueryAsync<UploadManagerRecentUploadRow>("""
                SELECT TOP 25 UploadId, OriginalFileName, CreatedByEmail, Status, TotalBytes, CreatedDate,
                       CompletedDate, LastActivityDate, Sha256Hash
                FROM dbo.ChunkedUploadSessions
                WHERE Status IN ('Complete','Failed','Cancelled')
                ORDER BY COALESCE(CompletedDate, LastActivityDate, CreatedDate) DESC;
                """)).ToList();
        }

        var transferTablesExist = await db.ExecuteScalarAsync<int>("""
            SELECT CASE WHEN OBJECT_ID('dbo.Transfers', 'U') IS NOT NULL AND OBJECT_ID('dbo.TransferFiles', 'U') IS NOT NULL THEN 1 ELSE 0 END
            """);

        if (transferTablesExist == 1)
        {
            dashboard.Storage = await db.QuerySingleAsync<TransferStorageSummary>("""
                SELECT
                    ActiveTransferCount = SUM(CASE WHEN t.Status = 'Active' AND t.ExpirationDate >= SYSUTCDATETIME() THEN 1 ELSE 0 END),
                    ExpiredTransferCount = SUM(CASE WHEN t.ExpirationDate < SYSUTCDATETIME() THEN 1 ELSE 0 END),
                    FileCount = COUNT(f.FileId),
                    TotalBytes = COALESCE(SUM(f.FileSizeBytes), 0),
                    ExpiredBytes = COALESCE(SUM(CASE WHEN t.ExpirationDate < SYSUTCDATETIME() THEN f.FileSizeBytes ELSE 0 END), 0),
                    BytesCreated24h = COALESCE(SUM(CASE WHEN t.CreatedDate >= DATEADD(day, -1, SYSUTCDATETIME()) THEN f.FileSizeBytes ELSE 0 END), 0),
                    OldestExpirationDate = MIN(t.ExpirationDate),
                    NewestTransferDate = MAX(t.CreatedDate)
                FROM dbo.Transfers t
                LEFT JOIN dbo.TransferFiles f ON f.TransferId = t.TransferId;
                """);

            dashboard.TopSenders = (await db.QueryAsync<TransferStorageBySenderRow>("""
                SELECT TOP 10
                    SenderEmail = COALESCE(NULLIF(t.SenderEmail, ''), '(unknown)'),
                    TransferCount = COUNT(DISTINCT t.TransferId),
                    FileCount = COUNT(f.FileId),
                    TotalBytes = COALESCE(SUM(f.FileSizeBytes), 0),
                    LastTransferDate = MAX(t.CreatedDate)
                FROM dbo.Transfers t
                LEFT JOIN dbo.TransferFiles f ON f.TransferId = t.TransferId
                GROUP BY COALESCE(NULLIF(t.SenderEmail, ''), '(unknown)')
                ORDER BY COALESCE(SUM(f.FileSizeBytes), 0) DESC;
                """)).ToList();
        }

        return dashboard;
    }

    public async Task SaveAsync(decimal maximumUploadGb, int maximumFilesPerTransfer, int requestTimeoutMinutes)
    {
        if (maximumUploadGb < 1 || maximumUploadGb > 500) throw new InvalidOperationException("Maximum upload size must be between 1 GB and 500 GB.");
        if (maximumFilesPerTransfer < 1 || maximumFilesPerTransfer > 10000) throw new InvalidOperationException("Maximum files per transfer must be between 1 and 10,000.");
        if (requestTimeoutMinutes < 5 || requestTimeoutMinutes > 2880) throw new InvalidOperationException("Request timeout must be between 5 and 2,880 minutes.");

        var bytes = (long)(maximumUploadGb * 1024m * 1024m * 1024m);

        await _settings.UpdateAsync("Upload.MaximumUploadGB", maximumUploadGb.ToString("0.##"));
        await _settings.UpdateAsync("Upload.MaximumUploadBytes", bytes.ToString());
        await _settings.UpdateAsync("Upload.MaximumFilesPerTransfer", maximumFilesPerTransfer.ToString());
        await _settings.UpdateAsync("Upload.RequestTimeoutMinutes", requestTimeoutMinutes.ToString());

        WriteWebConfigLimit(GetPublishWebConfigPath(), bytes, requestTimeoutMinutes);
    }

    public async Task SaveRetentionAsync(int retentionDays, bool automaticCleanupEnabled, int automaticCleanupHourUtc)
    {
        if (retentionDays < 1 || retentionDays > 3650) throw new InvalidOperationException("Retention days must be between 1 and 3,650.");
        if (automaticCleanupHourUtc < 0 || automaticCleanupHourUtc > 23) throw new InvalidOperationException("Automatic cleanup hour must be between 0 and 23 UTC.");

        await _settings.UpdateAsync("Retention.Days", retentionDays.ToString());
        await _settings.UpdateAsync("Retention.AutomaticCleanupEnabled", automaticCleanupEnabled ? "1" : "0");
        await _settings.UpdateAsync("Retention.AutomaticCleanupHourUtc", automaticCleanupHourUtc.ToString());
    }

    public async Task RepairAsync()
    {
        var bytes = await GetConfiguredBytesAsync();
        var timeout = await _settings.GetIntAsync("Upload.RequestTimeoutMinutes", 1440);
        WriteWebConfigLimit(GetPublishWebConfigPath(), bytes, timeout);
    }

    private async Task<long> GetConfiguredBytesAsync()
    {
        var text = await _settings.GetValueAsync("Upload.MaximumUploadBytes");
        return long.TryParse(text, out var bytes) && bytes > 0 ? bytes : 107374182400;
    }

    private string GetPublishWebConfigPath()
    {
        var configured = _config["Deployment:PublishWebConfigPath"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var root = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(root, "web.config"),
            Path.Combine(Directory.GetCurrentDirectory(), "Publish", "web.config"),
            Path.Combine("C:\\Build\\FileDrop_v1_starter\\Publish", "web.config")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates.Last();
    }

    private static long ReadWebConfigLimit(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            var attr = doc.Descendants("requestLimits").FirstOrDefault()?.Attribute("maxAllowedContentLength")?.Value;
            return long.TryParse(attr, out var value) ? value : 0;
        }
        catch { return 0; }
    }

    private static void WriteWebConfigLimit(string path, long bytes, int timeoutMinutes)
    {
        if (!File.Exists(path)) return;

        var doc = XDocument.Load(path);
        var config = doc.Element("configuration")!;
        var sws = config.Element("system.webServer") ?? new XElement("system.webServer");
        if (sws.Parent is null) config.Add(sws);

        var security = sws.Element("security") ?? new XElement("security");
        if (security.Parent is null) sws.Add(security);

        var filtering = security.Element("requestFiltering") ?? new XElement("requestFiltering");
        if (filtering.Parent is null) security.Add(filtering);

        var limits = filtering.Element("requestLimits") ?? new XElement("requestLimits");
        if (limits.Parent is null) filtering.Add(limits);

        limits.SetAttributeValue("maxAllowedContentLength", bytes.ToString());

        var aspNetCore = sws.Element("aspNetCore");
        if (aspNetCore is not null)
        {
            aspNetCore.SetAttributeValue("requestTimeout", $"{timeoutMinutes / 60:D2}:{timeoutMinutes % 60:D2}:00");
        }

        doc.Save(path);
    }
}
