using Dapper;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface ICleanupService
{
    Task<int> DeleteExpiredTransfersAsync(int olderThanDays = 0);
    Task<int> DeleteTransferFilesAndRecordAsync(Guid transferId);
    Task<int> DeleteUploadSessionAndFilesAsync(Guid uploadId);
    Task<CleanupPreviewResult> PreviewExpiredTransfersAsync(int olderThanDays = 0);
}

public sealed class CleanupService : ICleanupService
{
    private readonly string _connectionString;
    private readonly ILogger<CleanupService> _logger;

    public CleanupService(IConfiguration config, ILogger<CleanupService> logger)
    {
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
        _logger = logger;
    }

    public async Task<CleanupPreviewResult> PreviewExpiredTransfersAsync(int olderThanDays = 0)
    {
        if (olderThanDays < 0) olderThanDays = 0;
        await using var db = new SqlConnection(_connectionString);

        return await db.QuerySingleAsync<CleanupPreviewResult>("""
            SELECT
                TransferCount = COUNT(DISTINCT t.TransferId),
                FileCount = COUNT(f.FileId),
                TotalBytes = COALESCE(SUM(f.FileSizeBytes), 0)
            FROM dbo.Transfers t
            LEFT JOIN dbo.TransferFiles f ON f.TransferId = t.TransferId
            WHERE t.ExpirationDate < DATEADD(day, -@olderThanDays, SYSUTCDATETIME());
            """, new { olderThanDays });
    }

    public async Task<int> DeleteExpiredTransfersAsync(int olderThanDays = 0)
    {
        if (olderThanDays < 0) olderThanDays = 0;
        await using var db = new SqlConnection(_connectionString);

        var transfers = (await db.QueryAsync<Guid>("""
            SELECT TransferId
            FROM dbo.Transfers
            WHERE ExpirationDate < DATEADD(day, -@olderThanDays, SYSUTCDATETIME())
            """, new { olderThanDays })).ToList();

        var count = 0;
        foreach (var id in transfers)
        {
            count += await DeleteTransferFilesAndRecordAsync(id);
        }

        return count;
    }

    public async Task<int> DeleteTransferFilesAndRecordAsync(Guid transferId)
    {
        await using var db = new SqlConnection(_connectionString);

        var files = (await db.QueryAsync<string>("""
            SELECT StoragePath
            FROM dbo.TransferFiles
            WHERE TransferId = @transferId
            """, new { transferId })).ToList();

        foreach (var path in files)
        {
            DeleteFileIfExists(path);
        }

        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        await db.ExecuteAsync("DELETE FROM dbo.TransferFiles WHERE TransferId = @transferId", new { transferId }, tx);
        await db.ExecuteAsync("DELETE FROM dbo.DownloadNotificationLog WHERE TransferId = @transferId", new { transferId }, tx);
        var deleted = await db.ExecuteAsync("DELETE FROM dbo.Transfers WHERE TransferId = @transferId", new { transferId }, tx);

        await tx.CommitAsync();
        return deleted;
    }

    public async Task<int> DeleteUploadSessionAndFilesAsync(Guid uploadId)
    {
        await using var db = new SqlConnection(_connectionString);

        var upload = await db.QueryFirstOrDefaultAsync<UploadCleanupRow>("""
            SELECT UploadId, OriginalFileName, TempFolder, FinalStoragePath, StoredFileName, Status
            FROM dbo.ChunkedUploadSessions
            WHERE UploadId = @uploadId
            """, new { uploadId });

        if (upload is null)
        {
            return 0;
        }

        // Delete physical files first. Database cleanup still proceeds even if individual file deletes fail.
        DeleteFileIfExists(upload.FinalStoragePath);
        DeleteDirectoryIfExists(upload.TempFolder);

        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        await db.ExecuteAsync("DELETE FROM dbo.ChunkedUploadChunks WHERE UploadId = @uploadId", new { uploadId }, tx);
        var deleted = await db.ExecuteAsync("DELETE FROM dbo.ChunkedUploadSessions WHERE UploadId = @uploadId", new { uploadId }, tx);

        await tx.CommitAsync();
        return deleted;
    }

    private void DeleteFileIfExists(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete physical file {Path}", path);
        }
    }

    private void DeleteDirectoryIfExists(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete upload temp folder {Path}", path);
        }
    }

    private sealed class UploadCleanupRow
    {
        public Guid UploadId { get; set; }
        public string OriginalFileName { get; set; } = "";
        public string? TempFolder { get; set; }
        public string? FinalStoragePath { get; set; }
        public string? StoredFileName { get; set; }
        public string Status { get; set; } = "";
    }
}

public sealed class CleanupPreviewResult
{
    public int TransferCount { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
}
