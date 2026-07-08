namespace FileDrop.Web.Models;

public sealed class StartChunkedUploadRequest
{
    public string FileName { get; set; } = "";
    public string? ContentType { get; set; }
    public long TotalBytes { get; set; }
    public int ChunkSizeBytes { get; set; }
    public string? ClientFileId { get; set; }
    public long? LastModifiedTicks { get; set; }
    public string? ExpectedSha256Hash { get; set; }
}

public sealed class StartChunkedUploadResponse
{
    public Guid UploadId { get; set; }
    public int TotalChunks { get; set; }
    public int ChunkSizeBytes { get; set; }
    public string Status { get; set; } = "Uploading";
    public int[] CompletedChunks { get; set; } = Array.Empty<int>();
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public decimal Percent => TotalBytes <= 0 ? 0 : Math.Round(BytesReceived / (decimal)TotalBytes * 100, 1);
    public bool AlreadyComplete => Status.Equals("Complete", StringComparison.OrdinalIgnoreCase);
}

public class ChunkedUploadStatus
{
    public Guid UploadId { get; set; }
    public string Status { get; set; } = "";
    public int ChunksReceived { get; set; }
    public int TotalChunks { get; set; }
    public int[] CompletedChunks { get; set; } = Array.Empty<int>();
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public decimal Percent => TotalBytes <= 0 ? 0 : Math.Round(BytesReceived / (decimal)TotalBytes * 100, 1);
    public string? OriginalFileName { get; set; }
    public string? StoredFileName { get; set; }
    public string? StoragePath { get; set; }
    public string? Sha256Hash { get; set; }
    public string? ExpectedSha256Hash { get; set; }
    public string? ClientFileId { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public DateTime LastActivityDate { get; set; }
}

public sealed class SaveChunkResult : ChunkedUploadStatus
{
    public int ChunkIndex { get; set; }
    public bool AlreadyReceived { get; set; }
}

public sealed class CompleteChunkedUploadRequest
{
    public Guid UploadId { get; set; }
}

public sealed class CancelChunkedUploadRequest
{
    public Guid UploadId { get; set; }
}

public sealed class ActiveChunkedUploadSummary
{
    public Guid UploadId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string? CreatedByEmail { get; set; }
    public string Status { get; set; } = "";
    public int ChunksReceived { get; set; }
    public int TotalChunks { get; set; }
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime LastActivityDate { get; set; }
    public decimal Percent => TotalBytes <= 0 ? 0 : Math.Round(BytesReceived / (decimal)TotalBytes * 100, 1);
}

public sealed class ChunkedUploadedFile
{
    public Guid UploadId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string StoredFileName { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public string? ContentType { get; set; }
    public long FileSizeBytes { get; set; }
    public string? Sha256Hash { get; set; }
}
