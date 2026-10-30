using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

// Phase73B1 repository foundation. Endpoints and transfer-state integration must
// be completed and tested before enabling uploader override or approvals.
public sealed class PiiReviewRepository
{
    private readonly string _connectionString;
    public PiiReviewRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task<PiiReviewRequestRecord?> GetByUploadIdAsync(Guid uploadId)
    {
        await using var db = new SqlConnection(_connectionString);
        return await db.QuerySingleOrDefaultAsync<PiiReviewRequestRecord>(
            "SELECT * FROM dbo.PiiReviewRequests WHERE UploadId = @uploadId", new { uploadId });
    }

    public async Task<IReadOnlyList<PiiReviewRequestRecord>> ListPendingAsync()
    {
        await using var db = new SqlConnection(_connectionString);
        var rows = await db.QueryAsync<PiiReviewRequestRecord>(
            "SELECT TOP (100) * FROM dbo.PiiReviewRequests WHERE Status = 'Pending' ORDER BY RequestedUtc ASC");
        return rows.ToList();
    }

    public async Task<IReadOnlyList<PiiScanFindingRecord>> GetFindingsAsync(long scanId)
    {
        await using var db = new SqlConnection(_connectionString);
        return (await db.QueryAsync<PiiScanFindingRecord>(
            "SELECT FindingId, PiiScanId, Category, Confidence, PageNumber, MaskedEvidence, DetectionReason FROM dbo.PiiScanFindings WHERE PiiScanId = @scanId ORDER BY FindingId",
            new {scanId})).ToList();
    }

    public async Task<PiiReviewRequestRecord?> GetByIdAsync(long reviewId)
    {
        await using var db = new SqlConnection(_connectionString);
        return await db.QuerySingleOrDefaultAsync<PiiReviewRequestRecord>(
            "SELECT * FROM dbo.PiiReviewRequests WHERE ReviewId = @reviewId", new { reviewId });
    }

    public async Task<bool> IsUploadOwnedByAsync(Guid uploadId, string email)
    {
        await using var db = new SqlConnection(_connectionString);
        return await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(1) FROM dbo.ChunkedUploadSessions
            WHERE UploadId = @uploadId AND LOWER(CreatedByEmail) = LOWER(@email)
            """, new { uploadId, email }) == 1;
    }

    public async Task<bool> DecideAsync(long reviewId, string reviewer, bool approve, string reason)
    {
        if (reviewId < 1 || string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(reason)
            || reason.Trim().Length < 15 || reason.Length > 2000) return false;
        await using var db = new SqlConnection(_connectionString);
        // Review decision is audit-only in 73B2. No transfer changes or download release.
        return await db.ExecuteAsync("""
            UPDATE dbo.PiiReviewRequests
            SET Status = @decision, ReviewedBy = @reviewer, ReviewedUtc = SYSUTCDATETIME(),
                DecisionReason = @reason
            WHERE ReviewId = @reviewId AND Status = 'Pending'
              AND LOWER(RequestedBy) <> LOWER(@reviewer)
            """, new { reviewId, reviewer, reason = reason.Trim(), decision = approve ? "Approved" : "Rejected" }) == 1;
    }

    public async Task<long> InsertPendingAsync(Guid uploadId, long piiScanId, string requestedBy, string justification, string recipientEmail)
    {
        if (uploadId == Guid.Empty || piiScanId <= 0 || string.IsNullOrWhiteSpace(requestedBy)
            || string.IsNullOrWhiteSpace(recipientEmail) || recipientEmail.Length > 255
            || !System.Net.Mail.MailAddress.TryCreate(recipientEmail, out var parsedRecipient)
            || !string.Equals(parsedRecipient.Address, recipientEmail.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(justification) || justification.Trim().Length < 15)
            throw new ArgumentException("Valid upload, scan, authenticated user and 15-character justification required.");

        await using var db = new SqlConnection(_connectionString);
        return await db.ExecuteScalarAsync<long>("""
            INSERT INTO dbo.PiiReviewRequests (UploadId, PiiScanId, RequestedBy, Acknowledged, Justification, RecipientEmail)
            OUTPUT INSERTED.ReviewId
            SELECT @uploadId, @piiScanId, @requestedBy, 1, @justification, @recipientEmail
            WHERE EXISTS (
                SELECT 1 FROM dbo.UploadPiiScans
                WHERE PiiScanId = @piiScanId AND UploadId = @uploadId AND Allowed = 0
                  AND Result = 'Blocked'
            ) AND NOT EXISTS (SELECT 1 FROM dbo.PiiReviewRequests WHERE UploadId = @uploadId)
            """, new { uploadId, piiScanId, requestedBy, justification = justification.Trim(), recipientEmail = recipientEmail.Trim() });
    }
    // Soft-delete a pending review; retain evidence, audit fields and the held file.
    // Only the admin controller exposes this method.
    public async Task<bool> DeletePendingAsync(long reviewId, string administrator, string reason)
    {
        if (reviewId < 1 || string.IsNullOrWhiteSpace(administrator)
            || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 15 || reason.Length > 2000)
            return false;
        await using var db = new SqlConnection(_connectionString);
        return await db.ExecuteAsync("""
            UPDATE dbo.PiiReviewRequests
            SET Status = 'Deleted', ReviewedBy = @administrator,
                ReviewedUtc = SYSUTCDATETIME(), DecisionReason = @reason
            WHERE ReviewId = @reviewId AND Status = 'Pending'
            """, new { reviewId, administrator, reason = reason.Trim() }) == 1;
    }

}
