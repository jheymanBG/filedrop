using System.Text.Json;
using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IPiiScanRepository
{
    Task SaveAsync(Guid uploadId, string originalFileName, PiiScanResult result);
    Task SaveAcknowledgementAsync(Guid uploadId, string originalFileName, PiiScanRecord sourceRecord, string? acceptedBy);
    Task<PiiScanRecord?> GetLatestAsync(Guid uploadId);
    Task<List<PiiScanRecord>> GetRecentAsync(int count = 50);
}

public sealed class PiiScanRepository : IPiiScanRepository
{
    private readonly string _connectionString;

    public PiiScanRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task SaveAsync(Guid uploadId, string originalFileName, PiiScanResult result)
    {
        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync("""
            INSERT INTO dbo.UploadPiiScans
                (UploadId, OriginalFileName, Result, Allowed, FindingCount, Categories, Details, ScannedDate)
            VALUES
                (@UploadId, @OriginalFileName, @Result, @Allowed, @FindingCount, @Categories, @Details, SYSUTCDATETIME())
            """, new
        {
            UploadId = uploadId,
            OriginalFileName = Path.GetFileName(originalFileName),
            result.Result,
            result.Allowed,
            result.FindingCount,
            Categories = result.Categories,
            Details = JsonSerializer.Serialize(new
            {
                result.Provider,
                result.Message,
                result.RequiresAcknowledgement,
                result.HasHighConfidence,
                result.MaxConfidence,
                Findings = result.Findings.Select(x => new
                {
                    x.Category,
                    x.Count,
                    x.Confidence,
                    x.Location,
                    x.RedactedValue,
                    x.Reason
                })
            })
        });
    }

    public async Task SaveAcknowledgementAsync(Guid uploadId, string originalFileName, PiiScanRecord sourceRecord, string? acceptedBy)
    {
        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync("""
            INSERT INTO dbo.UploadPiiScans
                (UploadId, OriginalFileName, Result, Allowed, FindingCount, Categories, Details, ScannedDate)
            VALUES
                (@UploadId, @OriginalFileName, 'WarningAccepted', 1, @FindingCount, @Categories, @Details, SYSUTCDATETIME())
            """, new
        {
            UploadId = uploadId,
            OriginalFileName = Path.GetFileName(originalFileName),
            sourceRecord.FindingCount,
            sourceRecord.Categories,
            Details = $"User acknowledged low/medium confidence PII warning and continued. AcceptedBy={acceptedBy ?? "unknown"}; SourceScanId={sourceRecord.PiiScanId}. No unredacted PII values were logged."
        });
    }

    public async Task<PiiScanRecord?> GetLatestAsync(Guid uploadId)
    {
        await using var db = new SqlConnection(_connectionString);
        return await db.QuerySingleOrDefaultAsync<PiiScanRecord>("""
            SELECT TOP 1 PiiScanId, UploadId, OriginalFileName, Result, Allowed, FindingCount,
                   Categories, Details, ScannedDate
            FROM dbo.UploadPiiScans
            WHERE UploadId = @uploadId
            ORDER BY ScannedDate DESC, PiiScanId DESC
            """, new { uploadId });
    }

    public async Task<List<PiiScanRecord>> GetRecentAsync(int count = 50)
    {
        count = Math.Clamp(count, 1, 250);
        await using var db = new SqlConnection(_connectionString);
        return (await db.QueryAsync<PiiScanRecord>("""
            SELECT TOP (@count) PiiScanId, UploadId, OriginalFileName, Result, Allowed, FindingCount,
                   Categories, Details, ScannedDate
            FROM dbo.UploadPiiScans
            ORDER BY ScannedDate DESC, PiiScanId DESC
            """, new { count })).ToList();
    }
}
