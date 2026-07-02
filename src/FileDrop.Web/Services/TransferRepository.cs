using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface ITransferRepository
{
    Task CreateTransferAsync(TransferRecord transfer, IEnumerable<TransferFileRecord> files);
    Task<(TransferRecord? transfer, List<TransferFileRecord> files)> GetByTokenAsync(string token);
    Task MarkDownloadedAsync(Guid transferId);
}

public sealed class TransferRepository : ITransferRepository
{
    private readonly string _connectionString;

    public TransferRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task CreateTransferAsync(TransferRecord transfer, IEnumerable<TransferFileRecord> files)
    {
        await using var db = new SqlConnection(_connectionString);
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        await db.ExecuteAsync("""
            INSERT INTO dbo.Transfers
            (TransferId, SenderEmail, SenderName, RecipientEmail, Subject, Message, DownloadToken, ExpirationDate, MaxDownloads, DisableAfterFirstDownload)
            VALUES
            (@TransferId, @SenderEmail, @SenderName, @RecipientEmail, @Subject, @Message, @DownloadToken, @ExpirationDate, @MaxDownloads, @DisableAfterFirstDownload)
            """, transfer, tx);

        foreach (var file in files)
        {
            await db.ExecuteAsync("""
                INSERT INTO dbo.TransferFiles
                (FileId, TransferId, OriginalFileName, StoredFileName, StoragePath, ContentType, FileSizeBytes, Sha256Hash)
                VALUES
                (@FileId, @TransferId, @OriginalFileName, @StoredFileName, @StoragePath, @ContentType, @FileSizeBytes, @Sha256Hash)
                """, file, tx);
        }

        await tx.CommitAsync();
    }

    public async Task<(TransferRecord? transfer, List<TransferFileRecord> files)> GetByTokenAsync(string token)
    {
        await using var db = new SqlConnection(_connectionString);

        var transfer = await db.QueryFirstOrDefaultAsync<TransferRecord>(
            "SELECT * FROM dbo.Transfers WHERE DownloadToken = @token",
            new { token });

        if (transfer is null)
        {
            return (null, new List<TransferFileRecord>());
        }

        var files = (await db.QueryAsync<TransferFileRecord>(
            "SELECT * FROM dbo.TransferFiles WHERE TransferId = @id",
            new { id = transfer.TransferId })).ToList();

        return (transfer, files);
    }

    public async Task MarkDownloadedAsync(Guid transferId)
    {
        await using var db = new SqlConnection(_connectionString);

        await db.ExecuteAsync("""
            UPDATE dbo.Transfers
            SET DownloadCount = DownloadCount + 1,
                FirstDownloadDate = COALESCE(FirstDownloadDate, SYSUTCDATETIME()),
                LastDownloadDate = SYSUTCDATETIME(),
                Status = CASE
                    WHEN DisableAfterFirstDownload = 1 THEN 'Disabled'
                    WHEN MaxDownloads IS NOT NULL AND DownloadCount + 1 >= MaxDownloads THEN 'Disabled'
                    ELSE Status
                END
            WHERE TransferId = @transferId
            """, new { transferId });
    }
}


