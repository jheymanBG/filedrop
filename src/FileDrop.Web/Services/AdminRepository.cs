using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IAdminRepository
{
    Task<AdminDashboardViewModel> GetDashboardAsync();
    Task<List<AdminTransferSummary>> SearchTransfersAsync(string? query, string? status);
    Task<AdminTransferDetailViewModel?> GetTransferDetailAsync(Guid transferId);
    Task ExtendExpirationAsync(Guid transferId, int days);
    Task DisableTransferAsync(Guid transferId);
    Task DeleteTransferRecordAsync(Guid transferId);
}

public sealed class AdminRepository : IAdminRepository
{
    private readonly string _connectionString;

    public AdminRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task<AdminDashboardViewModel> GetDashboardAsync()
    {
        await using var db = new SqlConnection(_connectionString);

        var model = new AdminDashboardViewModel
        {
            ActiveTransfers = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Transfers WHERE Status = 'Active' AND ExpirationDate >= SYSUTCDATETIME()"),
            ExpiredTransfers = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Transfers WHERE ExpirationDate < SYSUTCDATETIME()"),
            TotalTransfers = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Transfers"),
            TotalFiles = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.TransferFiles"),
            TotalStorageBytes = await db.ExecuteScalarAsync<long>("SELECT COALESCE(SUM(FileSizeBytes),0) FROM dbo.TransferFiles"),
            DownloadsTotal = await db.ExecuteScalarAsync<int>("SELECT COALESCE(SUM(DownloadCount),0) FROM dbo.Transfers")
        };

        model.RecentTransfers = (await db.QueryAsync<AdminTransferSummary>("""
            SELECT TOP 15
                t.TransferId, t.SenderEmail, t.SenderName, t.RecipientEmail, t.Subject, t.DownloadToken,
                t.CreatedDate, t.ExpirationDate, t.DownloadCount, t.Status,
                COUNT(f.FileId) AS FileCount,
                COALESCE(SUM(f.FileSizeBytes),0) AS TotalBytes
            FROM dbo.Transfers t
            LEFT JOIN dbo.TransferFiles f ON f.TransferId = t.TransferId
            GROUP BY t.TransferId, t.SenderEmail, t.SenderName, t.RecipientEmail, t.Subject, t.DownloadToken,
                     t.CreatedDate, t.ExpirationDate, t.DownloadCount, t.Status
            ORDER BY t.CreatedDate DESC
            """)).ToList();

        model.LargestFiles = (await db.QueryAsync<AdminFileSummary>("""
            SELECT TOP 10 f.FileId, f.TransferId, f.OriginalFileName, f.FileSizeBytes, f.StoragePath, t.RecipientEmail, f.UploadedDate
            FROM dbo.TransferFiles f
            INNER JOIN dbo.Transfers t ON t.TransferId = f.TransferId
            ORDER BY f.FileSizeBytes DESC
            """)).ToList();

        return model;
    }

    public async Task<List<AdminTransferSummary>> SearchTransfersAsync(string? query, string? status)
    {
        await using var db = new SqlConnection(_connectionString);

        var sql = """
            SELECT TOP 200
                t.TransferId, t.SenderEmail, t.SenderName, t.RecipientEmail, t.Subject, t.DownloadToken,
                t.CreatedDate, t.ExpirationDate, t.DownloadCount, t.Status,
                COUNT(f.FileId) AS FileCount,
                COALESCE(SUM(f.FileSizeBytes),0) AS TotalBytes
            FROM dbo.Transfers t
            LEFT JOIN dbo.TransferFiles f ON f.TransferId = t.TransferId
            WHERE
                (@query IS NULL OR @query = '' OR
                 t.SenderEmail LIKE '%' + @query + '%' OR
                 t.RecipientEmail LIKE '%' + @query + '%' OR
                 t.Subject LIKE '%' + @query + '%' OR
                 EXISTS (SELECT 1 FROM dbo.TransferFiles fx WHERE fx.TransferId = t.TransferId AND fx.OriginalFileName LIKE '%' + @query + '%'))
                AND (@status IS NULL OR @status = '' OR t.Status = @status)
            GROUP BY t.TransferId, t.SenderEmail, t.SenderName, t.RecipientEmail, t.Subject, t.DownloadToken,
                     t.CreatedDate, t.ExpirationDate, t.DownloadCount, t.Status
            ORDER BY t.CreatedDate DESC
            """;

        return (await db.QueryAsync<AdminTransferSummary>(sql, new { query, status })).ToList();
    }

    public async Task<AdminTransferDetailViewModel?> GetTransferDetailAsync(Guid transferId)
    {
        await using var db = new SqlConnection(_connectionString);
        var transfer = await db.QueryFirstOrDefaultAsync<TransferRecord>("SELECT * FROM dbo.Transfers WHERE TransferId = @transferId", new { transferId });

        if (transfer is null)
        {
            return null;
        }

        var files = (await db.QueryAsync<TransferFileRecord>("SELECT * FROM dbo.TransferFiles WHERE TransferId = @transferId ORDER BY OriginalFileName", new { transferId })).ToList();

        return new AdminTransferDetailViewModel
        {
            Transfer = transfer,
            Files = files
        };
    }

    public async Task ExtendExpirationAsync(Guid transferId, int days)
    {
        if (days < 1 || days > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(days), "Days must be between 1 and 30.");
        }

        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync("""
            UPDATE dbo.Transfers
            SET ExpirationDate =
                CASE
                    WHEN ExpirationDate < SYSUTCDATETIME() THEN DATEADD(day, @days, SYSUTCDATETIME())
                    ELSE DATEADD(day, @days, ExpirationDate)
                END,
                Status = 'Active'
            WHERE TransferId = @transferId
            """, new { transferId, days });
    }

    public async Task DisableTransferAsync(Guid transferId)
    {
        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync("UPDATE dbo.Transfers SET Status = 'Disabled' WHERE TransferId = @transferId", new { transferId });
    }

    public async Task DeleteTransferRecordAsync(Guid transferId)
    {
        await using var db = new SqlConnection(_connectionString);
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        await db.ExecuteAsync("DELETE FROM dbo.TransferFiles WHERE TransferId = @transferId", new { transferId }, tx);
        await db.ExecuteAsync("DELETE FROM dbo.Transfers WHERE TransferId = @transferId", new { transferId }, tx);

        await tx.CommitAsync();
    }
}

