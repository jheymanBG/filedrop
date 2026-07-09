using System.Buffers;
using System.Security.Claims;
using System.Security.Cryptography;
using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IChunkedUploadService
{
    Task<StartChunkedUploadResponse> StartAsync(HttpContext context, StartChunkedUploadRequest request);
    Task<SaveChunkResult> SaveChunkAsync(Guid uploadId, int chunkIndex, IFormFile chunk, string? chunkSha256, CancellationToken cancellationToken);
    Task<ChunkedUploadStatus> CompleteAsync(Guid uploadId, CancellationToken cancellationToken);
    Task<ChunkedUploadStatus?> GetStatusAsync(Guid uploadId);
    Task<List<ActiveChunkedUploadSummary>> GetActiveAsync(HttpContext context);
    Task<ChunkedUploadStatus> CancelAsync(HttpContext context, Guid uploadId);
    Task<List<ChunkedUploadedFile>> GetCompletedAsync(IEnumerable<Guid> uploadIds);
    Task<int> CleanupAbandonedAsync(TimeSpan olderThan, CancellationToken cancellationToken);
}

public sealed class ChunkedUploadService : IChunkedUploadService
{
    private const string Uploading = "Uploading";
    private const string Finalizing = "Finalizing";
    private const string Complete = "Complete";
    private const string Failed = "Failed";

    private readonly IConfiguration _config;
    private readonly ISettingsRepository _settings;
    private readonly string _connectionString;
    private readonly ILogger<ChunkedUploadService> _logger;

    public ChunkedUploadService(IConfiguration config, ISettingsRepository settings, ILogger<ChunkedUploadService> logger)
    {
        _config = config;
        _settings = settings;
        _logger = logger;
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task<StartChunkedUploadResponse> StartAsync(HttpContext context, StartChunkedUploadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FileName)) throw new InvalidOperationException("File name is required.");
        if (request.TotalBytes <= 0) throw new InvalidOperationException("File size is required.");

        var maxBytesText = await _settings.GetValueAsync("Upload.MaximumUploadBytes");
        var maxBytes = long.TryParse(maxBytesText, out var parsedMax) ? parsedMax : 107374182400L;
        if (request.TotalBytes > maxBytes) throw new InvalidOperationException("File exceeds the configured maximum upload size.");

        var chunkSize = Math.Clamp(request.ChunkSizeBytes <= 0 ? 10485760 : request.ChunkSizeBytes, 1048576, 52428800);
        var totalChunks = (int)Math.Ceiling(request.TotalBytes / (double)chunkSize);
        var email = GetUserEmail(context);
        var clientFileId = NormalizeClientFileId(request.ClientFileId, request.FileName, request.TotalBytes, request.LastModifiedTicks);

        await using var db = new SqlConnection(_connectionString);

        // Phase 42: Only resume active in-progress sessions.
        // A completed, failed, cancelled, or abandoned upload must never be reused for a new upload attempt.
        // This allows users to upload the same file name/size/timestamp multiple times while preserving true resume
        // behavior for interrupted Uploading/Finalizing sessions that have the same ClientFileId.
        var existing = await db.QuerySingleOrDefaultAsync<ChunkedUploadStatusRow>("""
            SELECT TOP 1 *
            FROM dbo.ChunkedUploadSessions
            WHERE CreatedByEmail = @Email
              AND ClientFileId = @ClientFileId
              AND OriginalFileName = @FileName
              AND TotalBytes = @TotalBytes
              AND Status IN ('Uploading', 'Finalizing')
            ORDER BY CreatedDate DESC
            """, new
        {
            Email = email,
            ClientFileId = clientFileId,
            FileName = Path.GetFileName(request.FileName),
            request.TotalBytes
        });

        if (existing is not null)
        {
            var existingStatus = await BuildStatusAsync(db, existing);
            return new StartChunkedUploadResponse
            {
                UploadId = existing.UploadId,
                TotalChunks = existing.TotalChunks,
                ChunkSizeBytes = existing.ChunkSizeBytes,
                Status = existingStatus.Status,
                CompletedChunks = existingStatus.CompletedChunks,
                BytesReceived = existingStatus.BytesReceived,
                TotalBytes = existingStatus.TotalBytes
            };
        }

        var uploadId = Guid.NewGuid();
        var tempRoot = _config["Storage:TempPath"] ?? Path.Combine(_config["Storage:RootPath"] ?? "C:\\SecureFileTransfer", "Temp");
        var tempFolder = Path.Combine(tempRoot, uploadId.ToString("N"));
        Directory.CreateDirectory(tempFolder);

        await db.ExecuteAsync("""
            INSERT INTO dbo.ChunkedUploadSessions
            (UploadId, CreatedByEmail, OriginalFileName, ContentType, TotalBytes, ChunkSizeBytes, TotalChunks,
             TempFolder, ClientFileId, LastModifiedTicks, ExpectedSha256Hash, Status, CreatedDate, LastActivityDate)
            VALUES
            (@UploadId, @Email, @FileName, @ContentType, @TotalBytes, @ChunkSizeBytes, @TotalChunks,
             @TempFolder, @ClientFileId, @LastModifiedTicks, @ExpectedSha256Hash, 'Uploading', SYSUTCDATETIME(), SYSUTCDATETIME())
            """, new
        {
            UploadId = uploadId,
            Email = email,
            FileName = Path.GetFileName(request.FileName),
            request.ContentType,
            request.TotalBytes,
            ChunkSizeBytes = chunkSize,
            TotalChunks = totalChunks,
            TempFolder = tempFolder,
            ClientFileId = clientFileId,
            request.LastModifiedTicks,
            request.ExpectedSha256Hash
        });

        return new StartChunkedUploadResponse
        {
            UploadId = uploadId,
            TotalChunks = totalChunks,
            ChunkSizeBytes = chunkSize,
            Status = Uploading,
            CompletedChunks = Array.Empty<int>(),
            BytesReceived = 0,
            TotalBytes = request.TotalBytes
        };
    }

    public async Task<SaveChunkResult> SaveChunkAsync(Guid uploadId, int chunkIndex, IFormFile chunk, string? chunkSha256, CancellationToken cancellationToken)
    {
        await using var db = new SqlConnection(_connectionString);
        var session = await db.QuerySingleOrDefaultAsync<ChunkedUploadStatusRow>("SELECT * FROM dbo.ChunkedUploadSessions WHERE UploadId = @uploadId", new { uploadId });
        if (session is null) throw new InvalidOperationException("Upload session not found.");
        if (!session.Status.Equals(Uploading, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Upload session is not accepting chunks.");
        if (chunkIndex < 0 || chunkIndex >= session.TotalChunks) throw new InvalidOperationException("Invalid chunk index.");

        var expectedBytes = ExpectedChunkBytes(session, chunkIndex);
        if (chunk.Length != expectedBytes) throw new InvalidOperationException($"Chunk {chunkIndex} has invalid size. Expected {expectedBytes} bytes, received {chunk.Length} bytes.");

        Directory.CreateDirectory(session.TempFolder);
        var chunkPath = Path.Combine(session.TempFolder, $"{chunkIndex:D8}.chunk");

        var existingChunk = await db.QuerySingleOrDefaultAsync<ChunkRow>("""
            SELECT UploadId, ChunkIndex, BytesReceived, Sha256Hash
            FROM dbo.ChunkedUploadChunks
            WHERE UploadId = @uploadId AND ChunkIndex = @chunkIndex
            """, new { uploadId, chunkIndex });

        if (existingChunk is not null && File.Exists(chunkPath))
        {
            var status = await GetStatusAsync(uploadId) ?? throw new InvalidOperationException("Could not read upload status.");
            return CopyStatus(status, chunkIndex, alreadyReceived: true);
        }

        var tempPath = Path.Combine(session.TempFolder, $"{chunkIndex:D8}.{Guid.NewGuid():N}.uploading");
        string computedSha;
        await using (var output = File.Create(tempPath))
        await using (var input = chunk.OpenReadStream())
        using (var sha = SHA256.Create())
        await using (var crypto = new CryptoStream(output, sha, CryptoStreamMode.Write))
        {
            await input.CopyToAsync(crypto, cancellationToken);
            await crypto.FlushAsync(cancellationToken);
            crypto.FlushFinalBlock();
            computedSha = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(chunkSha256) && !computedSha.Equals(chunkSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(tempPath);
            throw new InvalidOperationException($"Chunk {chunkIndex} failed SHA256 verification.");
        }

        File.Move(tempPath, chunkPath, overwrite: true);

        await db.ExecuteAsync("""
            MERGE dbo.ChunkedUploadChunks WITH (HOLDLOCK) AS target
            USING (SELECT @UploadId AS UploadId, @ChunkIndex AS ChunkIndex) AS source
            ON target.UploadId = source.UploadId AND target.ChunkIndex = source.ChunkIndex
            WHEN NOT MATCHED THEN
                INSERT (UploadId, ChunkIndex, BytesReceived, Sha256Hash, CreatedDate)
                VALUES (@UploadId, @ChunkIndex, @BytesReceived, @Sha256Hash, SYSUTCDATETIME())
            WHEN MATCHED THEN
                UPDATE SET BytesReceived = @BytesReceived, Sha256Hash = @Sha256Hash, CreatedDate = SYSUTCDATETIME();

            UPDATE s
            SET ChunksReceived = x.ChunkCount,
                BytesReceived = x.TotalBytes,
                LastActivityDate = SYSUTCDATETIME()
            FROM dbo.ChunkedUploadSessions s
            CROSS APPLY (
                SELECT COUNT(1) AS ChunkCount, COALESCE(SUM(CAST(BytesReceived AS BIGINT)), 0) AS TotalBytes
                FROM dbo.ChunkedUploadChunks
                WHERE UploadId = @UploadId
            ) x
            WHERE s.UploadId = @UploadId;
            """, new { UploadId = uploadId, ChunkIndex = chunkIndex, BytesReceived = (int)chunk.Length, Sha256Hash = computedSha });

        var updated = await GetStatusAsync(uploadId) ?? throw new InvalidOperationException("Could not read upload status.");
        return CopyStatus(updated, chunkIndex, alreadyReceived: false);
    }

    public async Task<ChunkedUploadStatus> CompleteAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        await using var db = new SqlConnection(_connectionString);
        var session = await db.QuerySingleOrDefaultAsync<ChunkedUploadStatusRow>("SELECT * FROM dbo.ChunkedUploadSessions WHERE UploadId = @uploadId", new { uploadId });
        if (session is null) throw new InvalidOperationException("Upload session not found.");

        if (session.Status.Equals(Complete, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(session.FinalStoragePath) && File.Exists(session.FinalStoragePath))
            {
                return await BuildStatusAsync(db, session);
            }

            _logger.LogWarning("Upload session was marked complete but final file is missing. Resetting to Finalizing. UploadId={UploadId}; FinalStoragePath={FinalStoragePath}", uploadId, session.FinalStoragePath);
            await db.ExecuteAsync("""
                UPDATE dbo.ChunkedUploadSessions
                SET Status = 'Finalizing', CompletedDate = NULL, FinalStoragePath = NULL, StoredFileName = NULL, Sha256Hash = NULL, LastActivityDate = SYSUTCDATETIME()
                WHERE UploadId = @uploadId
                """, new { uploadId });
            session.Status = Finalizing;
            session.CompletedDate = null;
            session.FinalStoragePath = null;
            session.StoredFileName = null;
            session.Sha256Hash = null;
        }

        if (!session.Status.Equals(Uploading, StringComparison.OrdinalIgnoreCase) &&
            !session.Status.Equals(Finalizing, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Upload session cannot be completed.");
        }

        var chunkRows = (await db.QueryAsync<ChunkRow>("""
            SELECT UploadId, ChunkIndex, BytesReceived, Sha256Hash
            FROM dbo.ChunkedUploadChunks
            WHERE UploadId = @uploadId
            ORDER BY ChunkIndex
            """, new { uploadId })).ToList();

        var chunkMap = chunkRows.ToDictionary(x => x.ChunkIndex);
        if (chunkMap.Count != session.TotalChunks)
        {
            var missing = Enumerable.Range(0, session.TotalChunks).Where(i => !chunkMap.ContainsKey(i)).Take(20);
            throw new InvalidOperationException("Not all chunks have been uploaded yet. Missing: " + string.Join(", ", missing));
        }

        await db.ExecuteAsync("UPDATE dbo.ChunkedUploadSessions SET Status = 'Finalizing', LastActivityDate = SYSUTCDATETIME() WHERE UploadId = @uploadId", new { uploadId });

        var storageRoot = _config["Storage:RootPath"] ?? "C:\\SecureFileTransfer";
        var dateFolder = Path.Combine(storageRoot, DateTime.Now.ToString("yyyy"), DateTime.Now.ToString("MM"), DateTime.Now.ToString("dd"));
        var safeName = Path.GetFileName(session.OriginalFileName);
        var storedName = $"{Guid.NewGuid():N}-{safeName}";
        var finalPath = Path.Combine(dateFolder, storedName);
        var assemblingPath = finalPath + ".assembling";

        try
        {
            Directory.CreateDirectory(dateFolder);

            await ValidateChunksAsync(session, chunkMap, cancellationToken);

            TryDelete(assemblingPath);
            TryDelete(finalPath);

            var sha = await AssembleChunksAsync(session, assemblingPath, cancellationToken);

            var assembledInfo = new FileInfo(assemblingPath);
            if (!assembledInfo.Exists)
            {
                throw new InvalidOperationException("Final upload assembly did not create an output file.");
            }

            if (assembledInfo.Length != session.TotalBytes)
            {
                throw new InvalidOperationException($"Final upload size mismatch. Expected {session.TotalBytes} bytes, created {assembledInfo.Length} bytes.");
            }

            if (!string.IsNullOrWhiteSpace(session.ExpectedSha256Hash) &&
                !sha.Equals(session.ExpectedSha256Hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Final file failed SHA256 verification.");
            }

            File.Move(assemblingPath, finalPath, overwrite: true);

            var finalInfo = new FileInfo(finalPath);
            if (!finalInfo.Exists || finalInfo.Length != session.TotalBytes)
            {
                throw new InvalidOperationException("Final upload file was not available after move to storage.");
            }

            await db.ExecuteAsync("""
                UPDATE dbo.ChunkedUploadSessions
                SET Status = 'Complete', CompletedDate = SYSUTCDATETIME(), LastActivityDate = SYSUTCDATETIME(),
                    FinalStoragePath = @finalPath, StoredFileName = @storedName, Sha256Hash = @sha
                WHERE UploadId = @uploadId
                """, new { uploadId, finalPath, storedName, sha });

            TryDeleteDirectory(session.TempFolder);
            return await GetStatusAsync(uploadId) ?? throw new InvalidOperationException("Could not read upload status.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chunked upload finalization failed. UploadId={UploadId}; AssemblingPath={AssemblingPath}; FinalPath={FinalPath}", uploadId, assemblingPath, finalPath);
            TryDelete(assemblingPath);
            TryDelete(finalPath);
            await db.ExecuteAsync("UPDATE dbo.ChunkedUploadSessions SET Status = 'Failed', LastActivityDate = SYSUTCDATETIME() WHERE UploadId = @uploadId", new { uploadId });
            throw;
        }
    }

    public async Task<ChunkedUploadStatus?> GetStatusAsync(Guid uploadId)
    {
        await using var db = new SqlConnection(_connectionString);
        var row = await db.QuerySingleOrDefaultAsync<ChunkedUploadStatusRow>("SELECT * FROM dbo.ChunkedUploadSessions WHERE UploadId = @uploadId", new { uploadId });
        return row is null ? null : await BuildStatusAsync(db, row);
    }

    public async Task<List<ActiveChunkedUploadSummary>> GetActiveAsync(HttpContext context)
    {
        var email = GetUserEmail(context);
        await using var db = new SqlConnection(_connectionString);
        return (await db.QueryAsync<ActiveChunkedUploadSummary>("""
            SELECT TOP 50
                UploadId,
                OriginalFileName,
                CreatedByEmail,
                Status,
                ChunksReceived,
                TotalChunks,
                BytesReceived,
                TotalBytes,
                CreatedDate,
                LastActivityDate
            FROM dbo.ChunkedUploadSessions
            WHERE CreatedByEmail = @email
              AND Status IN ('Uploading','Finalizing')
            ORDER BY LastActivityDate DESC
            """, new { email })).ToList();
    }

    public async Task<ChunkedUploadStatus> CancelAsync(HttpContext context, Guid uploadId)
    {
        var email = GetUserEmail(context);
        await using var db = new SqlConnection(_connectionString);
        var session = await db.QuerySingleOrDefaultAsync<ChunkedUploadStatusRow>("""
            SELECT *
            FROM dbo.ChunkedUploadSessions
            WHERE UploadId = @uploadId AND CreatedByEmail = @email
            """, new { uploadId, email });

        if (session is null)
        {
            throw new InvalidOperationException("Upload session not found.");
        }

        if (session.Status.Equals(Complete, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Completed uploads cannot be cancelled.");
        }

        await db.ExecuteAsync("""
            UPDATE dbo.ChunkedUploadSessions
            SET Status = 'Cancelled', LastActivityDate = SYSUTCDATETIME()
            WHERE UploadId = @uploadId AND CreatedByEmail = @email AND Status <> 'Complete'
            """, new { uploadId, email });

        TryDeleteDirectory(session.TempFolder);
        return await GetStatusAsync(uploadId) ?? throw new InvalidOperationException("Could not read upload status.");
    }

    public async Task<List<ChunkedUploadedFile>> GetCompletedAsync(IEnumerable<Guid> uploadIds)
    {
        var ids = uploadIds.Distinct().ToArray();
        if (ids.Length == 0) return new List<ChunkedUploadedFile>();
        await using var db = new SqlConnection(_connectionString);
        return (await db.QueryAsync<ChunkedUploadedFile>("""
            SELECT UploadId, OriginalFileName, StoredFileName, FinalStoragePath AS StoragePath, ContentType,
                   TotalBytes AS FileSizeBytes, Sha256Hash
            FROM dbo.ChunkedUploadSessions
            WHERE UploadId IN @ids AND Status = 'Complete' AND FinalStoragePath IS NOT NULL
            """, new { ids })).ToList();
    }

    public async Task<int> CleanupAbandonedAsync(TimeSpan olderThan, CancellationToken cancellationToken)
    {
        await using var db = new SqlConnection(_connectionString);
        var rows = (await db.QueryAsync<ChunkedUploadStatusRow>("""
            SELECT * FROM dbo.ChunkedUploadSessions
            WHERE Status IN ('Uploading','Failed') AND LastActivityDate < DATEADD(minute, -@Minutes, SYSUTCDATETIME())
            """, new { Minutes = (int)Math.Ceiling(olderThan.TotalMinutes) })).ToList();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteDirectory(row.TempFolder);
        }

        if (rows.Count > 0)
        {
            await db.ExecuteAsync("""
                UPDATE dbo.ChunkedUploadSessions
                SET Status = 'Abandoned'
                WHERE Status IN ('Uploading','Failed') AND LastActivityDate < DATEADD(minute, -@Minutes, SYSUTCDATETIME())
                """, new { Minutes = (int)Math.Ceiling(olderThan.TotalMinutes) });
        }

        return rows.Count;
    }

    private async Task<ChunkedUploadStatus> BuildStatusAsync(SqlConnection db, ChunkedUploadStatusRow row)
    {
        var completed = (await db.QueryAsync<int>("SELECT ChunkIndex FROM dbo.ChunkedUploadChunks WHERE UploadId = @UploadId ORDER BY ChunkIndex", new { row.UploadId })).ToArray();
        var bytes = await db.QuerySingleAsync<long>("SELECT COALESCE(SUM(CAST(BytesReceived AS BIGINT)), 0) FROM dbo.ChunkedUploadChunks WHERE UploadId = @UploadId", new { row.UploadId });
        return new ChunkedUploadStatus
        {
            UploadId = row.UploadId,
            Status = row.Status,
            ChunksReceived = completed.Length,
            TotalChunks = row.TotalChunks,
            CompletedChunks = completed,
            BytesReceived = bytes,
            TotalBytes = row.TotalBytes,
            OriginalFileName = row.OriginalFileName,
            StoredFileName = row.StoredFileName,
            StoragePath = row.FinalStoragePath,
            Sha256Hash = row.Sha256Hash,
            ExpectedSha256Hash = row.ExpectedSha256Hash,
            ClientFileId = row.ClientFileId,
            CreatedDate = row.CreatedDate,
            CompletedDate = row.CompletedDate,
            LastActivityDate = row.LastActivityDate
        };
    }

    private static SaveChunkResult CopyStatus(ChunkedUploadStatus status, int chunkIndex, bool alreadyReceived) => new()
    {
        UploadId = status.UploadId,
        Status = status.Status,
        ChunksReceived = status.ChunksReceived,
        TotalChunks = status.TotalChunks,
        CompletedChunks = status.CompletedChunks,
        BytesReceived = status.BytesReceived,
        TotalBytes = status.TotalBytes,
        OriginalFileName = status.OriginalFileName,
        StoredFileName = status.StoredFileName,
        StoragePath = status.StoragePath,
        Sha256Hash = status.Sha256Hash,
        ExpectedSha256Hash = status.ExpectedSha256Hash,
        ClientFileId = status.ClientFileId,
        CreatedDate = status.CreatedDate,
        CompletedDate = status.CompletedDate,
        LastActivityDate = status.LastActivityDate,
        ChunkIndex = chunkIndex,
        AlreadyReceived = alreadyReceived
    };

    private static long ExpectedChunkBytes(ChunkedUploadStatusRow session, int chunkIndex)
    {
        var start = (long)chunkIndex * session.ChunkSizeBytes;
        return Math.Min(session.ChunkSizeBytes, session.TotalBytes - start);
    }

    private async Task ValidateChunksAsync(ChunkedUploadStatusRow session, IReadOnlyDictionary<int, ChunkRow> chunkMap, CancellationToken cancellationToken)
    {
        var verifySha = _config.GetValue<bool>("Upload:VerifyChunkShaBeforeAssembly", false);
        var maxParallel = _config.GetValue<int?>("Upload:ChunkValidationDegreeOfParallelism") ?? Math.Min(Environment.ProcessorCount, 8);
        maxParallel = Math.Clamp(maxParallel, 1, 16);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallel,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, session.TotalChunks), options, async (chunkIndex, ct) =>
        {
            if (!chunkMap.TryGetValue(chunkIndex, out var row))
            {
                throw new InvalidOperationException($"Chunk {chunkIndex} is missing from the upload database record.");
            }

            var chunkPath = Path.Combine(session.TempFolder, $"{chunkIndex:D8}.chunk");
            var info = new FileInfo(chunkPath);
            if (!info.Exists)
            {
                throw new InvalidOperationException($"Chunk file {chunkIndex} is missing from temporary storage.");
            }

            var expectedBytes = ExpectedChunkBytes(session, chunkIndex);
            if (info.Length != expectedBytes || row.BytesReceived != expectedBytes)
            {
                throw new InvalidOperationException($"Chunk {chunkIndex} has invalid size. Expected {expectedBytes} bytes, found {info.Length} bytes on disk and {row.BytesReceived} bytes in SQL.");
            }

            if (verifySha && !string.IsNullOrWhiteSpace(row.Sha256Hash))
            {
                var sha = await ComputeSha256Async(chunkPath, ct);
                if (!sha.Equals(row.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Chunk {chunkIndex} failed SHA256 verification before assembly.");
                }
            }
        });
    }

    private async Task<string> AssembleChunksAsync(ChunkedUploadStatusRow session, string outputPath, CancellationToken cancellationToken)
    {
        var bufferSize = _config.GetValue<int?>("Upload:AssemblyBufferBytes") ?? (8 * 1024 * 1024);
        bufferSize = Math.Clamp(bufferSize, 1024 * 1024, 32 * 1024 * 1024);

        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            await using var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            for (var i = 0; i < session.TotalChunks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkPath = Path.Combine(session.TempFolder, $"{i:D8}.chunk");

                await using var input = new FileStream(
                    chunkPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    incrementalHash.AppendData(buffer, 0, read);
                }
            }

            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(incrementalHash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string GetUserEmail(HttpContext context) =>
        context.User?.FindFirstValue("preferred_username") ??
        context.User?.FindFirstValue(ClaimTypes.Email) ??
        context.User?.Identity?.Name ??
        "unknown";

    private static string NormalizeClientFileId(string? clientFileId, string fileName, long totalBytes, long? lastModifiedTicks)
    {
        var raw = string.IsNullOrWhiteSpace(clientFileId) ? $"{Path.GetFileName(fileName)}|{totalBytes}|{lastModifiedTicks}" : clientFileId.Trim();
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed class ChunkedUploadStatusRow
    {
        public Guid UploadId { get; set; }
        public string OriginalFileName { get; set; } = "";
        public string? ContentType { get; set; }
        public long TotalBytes { get; set; }
        public int ChunkSizeBytes { get; set; }
        public int TotalChunks { get; set; }
        public int ChunksReceived { get; set; }
        public long BytesReceived { get; set; }
        public string TempFolder { get; set; } = "";
        public string? FinalStoragePath { get; set; }
        public string? StoredFileName { get; set; }
        public string? Sha256Hash { get; set; }
        public string? ExpectedSha256Hash { get; set; }
        public string? ClientFileId { get; set; }
        public string? CreatedByEmail { get; set; }
        public string Status { get; set; } = "";
        public DateTime CreatedDate { get; set; }
        public DateTime? CompletedDate { get; set; }
        public DateTime LastActivityDate { get; set; }
    }

    private sealed class ChunkRow
    {
        public Guid UploadId { get; set; }
        public int ChunkIndex { get; set; }
        public int BytesReceived { get; set; }
        public string? Sha256Hash { get; set; }
    }
}
