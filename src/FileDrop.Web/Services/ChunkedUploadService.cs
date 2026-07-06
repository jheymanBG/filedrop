using System.Security.Claims;
using System.Security.Cryptography;
using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IChunkedUploadService
{
    Task<StartChunkedUploadResponse> StartAsync(HttpContext context, StartChunkedUploadRequest request);
    Task<ChunkedUploadStatus> SaveChunkAsync(Guid uploadId, int chunkIndex, IFormFile chunk, CancellationToken cancellationToken);
    Task<ChunkedUploadStatus> CompleteAsync(Guid uploadId, CancellationToken cancellationToken);
    Task<ChunkedUploadStatus?> GetStatusAsync(Guid uploadId);
    Task<List<ChunkedUploadedFile>> GetCompletedAsync(IEnumerable<Guid> uploadIds);
}

public sealed class ChunkedUploadService : IChunkedUploadService
{
    private readonly IConfiguration _config;
    private readonly ISettingsRepository _settings;
    private readonly string _connectionString;

    public ChunkedUploadService(IConfiguration config, ISettingsRepository settings)
    {
        _config = config;
        _settings = settings;
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task<StartChunkedUploadResponse> StartAsync(HttpContext context, StartChunkedUploadRequest request)
    {
        var maxBytesText = await _settings.GetValueAsync("Upload.MaximumUploadBytes");
        var maxBytes = long.TryParse(maxBytesText, out var parsedMax) ? parsedMax : 107374182400;

        if (request.TotalBytes <= 0)
        {
            throw new InvalidOperationException("File size is required.");
        }

        if (request.TotalBytes > maxBytes)
        {
            throw new InvalidOperationException("File exceeds the configured maximum upload size.");
        }

        var chunkSize = Math.Clamp(request.ChunkSizeBytes <= 0 ? 10485760 : request.ChunkSizeBytes, 1048576, 52428800);
        var totalChunks = (int)Math.Ceiling(request.TotalBytes / (double)chunkSize);
        var uploadId = Guid.NewGuid();

        var tempRoot = _config["Storage:TempPath"] ?? Path.Combine(_config["Storage:RootPath"] ?? "C:\\SecureFileTransfer", "Temp");
        var tempFolder = Path.Combine(tempRoot, uploadId.ToString("N"));
        Directory.CreateDirectory(tempFolder);

        var email = context.User?.FindFirstValue("preferred_username") ??
                    context.User?.FindFirstValue(ClaimTypes.Email) ??
                    context.User?.Identity?.Name;

        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync("""
            INSERT INTO dbo.ChunkedUploadSessions
            (UploadId, CreatedByEmail, OriginalFileName, ContentType, TotalBytes, ChunkSizeBytes, TotalChunks, TempFolder)
            VALUES
            (@UploadId, @Email, @FileName, @ContentType, @TotalBytes, @ChunkSizeBytes, @TotalChunks, @TempFolder)
            """, new
        {
            UploadId = uploadId,
            Email = email,
            FileName = Path.GetFileName(request.FileName),
            request.ContentType,
            request.TotalBytes,
            ChunkSizeBytes = chunkSize,
            TotalChunks = totalChunks,
            TempFolder = tempFolder
        });

        return new StartChunkedUploadResponse
        {
            UploadId = uploadId,
            TotalChunks = totalChunks,
            ChunkSizeBytes = chunkSize
        };
    }

    public async Task<ChunkedUploadStatus> SaveChunkAsync(Guid uploadId, int chunkIndex, IFormFile chunk, CancellationToken cancellationToken)
    {
        await using var db = new SqlConnection(_connectionString);

        var session = await db.QuerySingleOrDefaultAsync<ChunkedUploadStatusRow>(
            "SELECT * FROM dbo.ChunkedUploadSessions WHERE UploadId = @uploadId",
            new { uploadId });

        if (session is null)
        {
            throw new InvalidOperationException("Upload session not found.");
        }

        if (!session.Status.Equals("Uploading", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Upload session is not accepting chunks.");
        }

        if (chunkIndex < 0 || chunkIndex >= session.TotalChunks)
        {
            throw new InvalidOperationException("Invalid chunk index.");
        }

        Directory.CreateDirectory(session.TempFolder);
        var chunkPath = Path.Combine(session.TempFolder, $"{chunkIndex:D8}.chunk");

        await using (var output = File.Create(chunkPath))
        await using (var input = chunk.OpenReadStream())
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        await db.ExecuteAsync("""
            IF NOT EXISTS (SELECT 1 FROM dbo.ChunkedUploadChunks WHERE UploadId = @uploadId AND ChunkIndex = @chunkIndex)
            BEGIN
                INSERT INTO dbo.ChunkedUploadChunks (UploadId, ChunkIndex, BytesReceived)
                VALUES (@uploadId, @chunkIndex, @bytesReceived);

                UPDATE dbo.ChunkedUploadSessions
                SET ChunksReceived = ChunksReceived + 1,
                    BytesReceived = BytesReceived + @bytesReceived
                WHERE UploadId = @uploadId;
            END
            """, new { uploadId, chunkIndex, bytesReceived = (int)chunk.Length });

        return await GetStatusAsync(uploadId) ?? throw new InvalidOperationException("Could not read upload status.");
    }

    public async Task<ChunkedUploadStatus> CompleteAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        await using var db = new SqlConnection(_connectionString);

        var session = await db.QuerySingleOrDefaultAsync<ChunkedUploadStatusRow>(
            "SELECT * FROM dbo.ChunkedUploadSessions WHERE UploadId = @uploadId",
            new { uploadId });

        if (session is null)
        {
            throw new InvalidOperationException("Upload session not found.");
        }

        if (session.ChunksReceived != session.TotalChunks)
        {
            throw new InvalidOperationException("Not all chunks have been uploaded yet.");
        }

        var storageRoot = _config["Storage:RootPath"] ?? "C:\\SecureFileTransfer";
        var dateFolder = Path.Combine(storageRoot, DateTime.Now.ToString("yyyy"), DateTime.Now.ToString("MM"), DateTime.Now.ToString("dd"));
        Directory.CreateDirectory(dateFolder);

        var safeName = Path.GetFileName(session.OriginalFileName);
        var storedName = $"{Guid.NewGuid():N}-{safeName}";
        var finalPath = Path.Combine(dateFolder, storedName);

        await using (var output = File.Create(finalPath))
        {
            for (var i = 0; i < session.TotalChunks; i++)
            {
                var chunkPath = Path.Combine(session.TempFolder, $"{i:D8}.chunk");

                if (!File.Exists(chunkPath))
                {
                    throw new InvalidOperationException($"Missing chunk {i}.");
                }

                await using var input = File.OpenRead(chunkPath);
                await input.CopyToAsync(output, cancellationToken);
            }
        }

        var sha = await ComputeSha256Async(finalPath, cancellationToken);

        await db.ExecuteAsync("""
            UPDATE dbo.ChunkedUploadSessions
            SET Status = 'Complete',
                CompletedDate = SYSUTCDATETIME(),
                FinalStoragePath = @finalPath,
                StoredFileName = @storedName,
                Sha256Hash = @sha
            WHERE UploadId = @uploadId
            """, new { uploadId, finalPath, storedName, sha });

        try
        {
            Directory.Delete(session.TempFolder, true);
        }
        catch
        {
            // Not fatal.
        }

        return await GetStatusAsync(uploadId) ?? throw new InvalidOperationException("Could not read upload status.");
    }

    public async Task<ChunkedUploadStatus?> GetStatusAsync(Guid uploadId)
    {
        await using var db = new SqlConnection(_connectionString);

        var row = await db.QuerySingleOrDefaultAsync<ChunkedUploadStatusRow>(
            "SELECT * FROM dbo.ChunkedUploadSessions WHERE UploadId = @uploadId",
            new { uploadId });

        return row is null ? null : new ChunkedUploadStatus
        {
            UploadId = row.UploadId,
            Status = row.Status,
            ChunksReceived = row.ChunksReceived,
            TotalChunks = row.TotalChunks,
            BytesReceived = row.BytesReceived,
            TotalBytes = row.TotalBytes,
            OriginalFileName = row.OriginalFileName,
            StoredFileName = row.StoredFileName,
            StoragePath = row.FinalStoragePath,
            Sha256Hash = row.Sha256Hash
        };
    }

    public async Task<List<ChunkedUploadedFile>> GetCompletedAsync(IEnumerable<Guid> uploadIds)
    {
        var ids = uploadIds.ToArray();

        if (ids.Length == 0)
        {
            return new List<ChunkedUploadedFile>();
        }

        await using var db = new SqlConnection(_connectionString);

        return (await db.QueryAsync<ChunkedUploadedFile>("""
            SELECT UploadId,
                   OriginalFileName,
                   StoredFileName,
                   FinalStoragePath AS StoragePath,
                   ContentType,
                   TotalBytes AS FileSizeBytes,
                   Sha256Hash
            FROM dbo.ChunkedUploadSessions
            WHERE UploadId IN @ids
              AND Status = 'Complete'
            """, new { ids })).ToList();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class ChunkedUploadStatusRow
    {
        public Guid UploadId { get; set; }
        public string OriginalFileName { get; set; } = "";
        public string? ContentType { get; set; }
        public long TotalBytes { get; set; }
        public int TotalChunks { get; set; }
        public int ChunksReceived { get; set; }
        public long BytesReceived { get; set; }
        public string TempFolder { get; set; } = "";
        public string? FinalStoragePath { get; set; }
        public string? StoredFileName { get; set; }
        public string? Sha256Hash { get; set; }
        public string Status { get; set; } = "";
    }
}
