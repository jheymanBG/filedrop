namespace FileDrop.Web.Models;

public sealed class StartChunkedUploadRequest
{
    public string FileName { get; set; } = "";
    public string? ContentType { get; set; }
    public long TotalBytes { get; set; }
    public int ChunkSizeBytes { get; set; }
}

public sealed class StartChunkedUploadResponse
{
    public Guid UploadId { get; set; }
    public int TotalChunks { get; set; }
    public int ChunkSizeBytes { get; set; }
}

public sealed class ChunkedUploadStatus
{
    public Guid UploadId { get; set; }
    public string Status { get; set; } = "";
    public int ChunksReceived { get; set; }
    public int TotalChunks { get; set; }
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public decimal Percent => TotalBytes <= 0 ? 0 : Math.Round(BytesReceived / (decimal)TotalBytes * 100, 1);
    public string? OriginalFileName { get; set; }
    public string? StoredFileName { get; set; }
    public string? StoragePath { get; set; }
    public string? Sha256Hash { get; set; }
}

public sealed class CompleteChunkedUploadRequest
{
    public Guid UploadId { get; set; }
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
