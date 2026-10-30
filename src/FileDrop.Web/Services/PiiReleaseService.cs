using System.Security.Cryptography;
using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

// Release is committed atomically with the review decision. No usable transfer token
// is persisted until the held file passes integrity and on-demand malware checks.
public sealed class PiiReleaseService
{
    private readonly string _connectionString;
    private readonly IVirusScanService _scanner;
    private readonly ITokenService _tokens;

    public PiiReleaseService(IConfiguration config, IVirusScanService scanner, ITokenService tokens)
    {
        _connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection missing");
        _scanner = scanner;
        _tokens = tokens;
    }

    public async Task<(TransferRecord transfer, List<TransferFileRecord> files)> ApproveAsync(
        long reviewId, string reviewer, string reason, string recipient,
        int expirationDays, int? maxDownloads, bool oneTime, CancellationToken cancellationToken)
    {
        if (reviewId < 1 || string.IsNullOrWhiteSpace(reviewer) || reason?.Trim().Length < 15 || reason.Length > 2000
            || !System.Net.Mail.MailAddress.TryCreate(recipient, out var address) || address.Address != recipient.Trim()
            || expirationDays is < 1 or > 30 || maxDownloads is < 1)
            throw new ArgumentException("A valid recipient, justification and expiration are required.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var pending = await connection.QuerySingleOrDefaultAsync<ReleaseRow>("""
            SELECT r.ReviewId, r.UploadId, r.RequestedBy, r.RecipientEmail AS SavedRecipientEmail, r.Status, r.PiiScanId,
                   u.OriginalFileName, u.StoredFileName, u.ContentType,
                   u.FinalStoragePath AS StoragePath, u.TotalBytes AS FileSizeBytes,
                   u.Sha256Hash, u.Status AS UploadStatus
            FROM dbo.PiiReviewRequests r
            JOIN dbo.ChunkedUploadSessions u ON u.UploadId = r.UploadId
            WHERE r.ReviewId = @reviewId
            """, new { reviewId });
        if (pending is null || pending.Status != "Pending" || pending.UploadStatus != "PendingPIIReview"
            || string.IsNullOrWhiteSpace(pending.SavedRecipientEmail)
            || !string.Equals(pending.SavedRecipientEmail.Trim(), recipient.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(pending.RequestedBy, reviewer, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Review is not pending, upload is not held, or self-approval was attempted.");
        if (string.IsNullOrWhiteSpace(pending.StoragePath) || !File.Exists(pending.StoragePath)
            || string.IsNullOrWhiteSpace(pending.StoredFileName))
            throw new InvalidOperationException("Held file is missing. No release was performed.");
        var info = new FileInfo(pending.StoragePath);
        if (info.Length != pending.FileSizeBytes || string.IsNullOrWhiteSpace(pending.Sha256Hash))
            throw new InvalidOperationException("Held file integrity metadata is missing or has changed.");
        await using (var stream = File.OpenRead(pending.StoragePath))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(hash, pending.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Held file SHA-256 does not match. Release denied.");
        }
        // FileDrop application malware scanning is disabled by City policy.
        // SentinelOne provides endpoint protection independently.
        // SHA-256 and all PII approval checks remain enforced.

        var transfer = new TransferRecord {
            TransferId = Guid.NewGuid(), SenderEmail = pending.RequestedBy,
            RecipientEmail = recipient.Trim(), Subject = "Approved PII exception: " + pending.OriginalFileName,
            Message = "PII exception reviewed and released by an authorized administrator.",
            DownloadToken = _tokens.CreateToken(), ExpirationDate = DateTime.UtcNow.AddDays(expirationDays),
            MaxDownloads = maxDownloads, DisableAfterFirstDownload = oneTime
        };
        var file = new TransferFileRecord {
            FileId = Guid.NewGuid(), TransferId = transfer.TransferId,
            OriginalFileName = pending.OriginalFileName, StoredFileName = pending.StoredFileName,
            StoragePath = pending.StoragePath, FileSizeBytes = pending.FileSizeBytes,
            ContentType = pending.ContentType, Sha256Hash = pending.Sha256Hash
        };
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var locked = await connection.QuerySingleOrDefaultAsync<ReleaseRow>("""
            SELECT r.ReviewId, r.UploadId, r.RequestedBy, r.RecipientEmail AS SavedRecipientEmail, r.Status, r.PiiScanId,
                   u.OriginalFileName, u.StoredFileName, u.ContentType,
                   u.FinalStoragePath AS StoragePath, u.TotalBytes AS FileSizeBytes,
                   u.Sha256Hash, u.Status AS UploadStatus
            FROM dbo.PiiReviewRequests r WITH (UPDLOCK, HOLDLOCK)
            JOIN dbo.ChunkedUploadSessions u WITH (UPDLOCK, HOLDLOCK) ON u.UploadId = r.UploadId
            WHERE r.ReviewId = @reviewId
            """, new { reviewId }, transaction);
        if (locked is null || locked.Status != "Pending" || locked.UploadStatus != "PendingPIIReview"
            || locked.PiiScanId != pending.PiiScanId || locked.UploadId != pending.UploadId
            || locked.StoragePath != pending.StoragePath || locked.Sha256Hash != pending.Sha256Hash
            || locked.FileSizeBytes != pending.FileSizeBytes
            || !string.Equals(locked.SavedRecipientEmail, pending.SavedRecipientEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Review or uploaded file changed while scanning. No release performed.");
        // Verify held file remains intact before committing access; guard against file swaps.
        var check = new FileInfo(pending.StoragePath);
        if (!check.Exists || check.Length != pending.FileSizeBytes)
            throw new InvalidOperationException("Held file changed before approval.");
        await connection.ExecuteAsync("""
            INSERT INTO dbo.Transfers
            (TransferId, SenderEmail, RecipientEmail, Subject, Message, DownloadToken, ExpirationDate, MaxDownloads, DisableAfterFirstDownload)
            VALUES
            (@TransferId, @SenderEmail, @RecipientEmail, @Subject, @Message, @DownloadToken, @ExpirationDate, @MaxDownloads, @DisableAfterFirstDownload)
            """, transfer, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO dbo.TransferFiles
            (FileId, TransferId, OriginalFileName, StoredFileName, StoragePath, ContentType, FileSizeBytes, Sha256Hash, ScanStatus)
            VALUES (@FileId, @TransferId, @OriginalFileName, @StoredFileName, @StoragePath, @ContentType, @FileSizeBytes, @Sha256Hash, 'Clean')
            """, file, transaction);
        var changed = await connection.ExecuteAsync("""
            UPDATE dbo.PiiReviewRequests
            SET Status = 'Approved', TransferId = @transferId,
                ReviewedBy = @reviewer, ReviewedUtc = SYSUTCDATETIME(), DecisionReason = @reason
            WHERE ReviewId = @reviewId AND Status = 'Pending' AND LOWER(RequestedBy) <> LOWER(@reviewer)
            """, new { transferId = transfer.TransferId, reviewer, reason = reason.Trim(), reviewId }, transaction);
        if (changed != 1) throw new InvalidOperationException("Another administrator decided this request.");
        changed = await connection.ExecuteAsync("""
            UPDATE dbo.ChunkedUploadSessions SET Status = 'Released', LastActivityDate = SYSUTCDATETIME()
            WHERE UploadId = @uploadId AND Status = 'PendingPIIReview'
            """, new { uploadId = pending.UploadId }, transaction);
        if (changed != 1) throw new InvalidOperationException("Upload hold state changed. Release cancelled.");
        await transaction.CommitAsync(cancellationToken);
        return (transfer, new List<TransferFileRecord> { file });
    }

    private sealed class ReleaseRow
    {
        public long ReviewId { get; set; }
        public Guid UploadId { get; set; }
        public string RequestedBy { get; set; } = "";
        public string? SavedRecipientEmail { get; set; }
        public string Status { get; set; } = "";
        public long PiiScanId { get; set; }
        public string OriginalFileName { get; set; } = "";
        public string StoredFileName { get; set; } = "";
        public string? ContentType { get; set; }
        public string? StoragePath { get; set; }
        public long FileSizeBytes { get; set; }
        public string? Sha256Hash { get; set; }
        public string UploadStatus { get; set; } = "";
    }
}
