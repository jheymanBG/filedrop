namespace FileDrop.Web.Models;

public sealed class TransferRecord
{
    public Guid TransferId { get; set; }
    public string SenderEmail { get; set; } = "";
    public string? SenderName { get; set; }
    public string RecipientEmail { get; set; } = "";
    public string? Subject { get; set; }
    public string? Message { get; set; }
    public string DownloadToken { get; set; } = "";
    public DateTime CreatedDate { get; set; }
    public DateTime ExpirationDate { get; set; }
    public int DownloadCount { get; set; }
    public string Status { get; set; } = "Active";
    public int? MaxDownloads { get; set; }
    public bool DisableAfterFirstDownload { get; set; }
}

public sealed class TransferFileRecord
{
    public Guid FileId { get; set; }
    public Guid TransferId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string StoredFileName { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public string? ContentType { get; set; }
    public long FileSizeBytes { get; set; }
    public string? Sha256Hash { get; set; }
}

public sealed class DownloadViewModel
{
    public TransferRecord Transfer { get; set; } = new();
    public List<TransferFileRecord> Files { get; set; } = new();
}

public sealed class CreateTransferFromUploadsRequest
{
    public List<Guid> UploadedIds { get; set; } = new();
    public string RecipientEmail { get; set; } = "";
    public string? Subject { get; set; }
    public string? Message { get; set; }
    public int ExpirationDays { get; set; } = 7;
    public int? MaxDownloads { get; set; }
    public bool DisableAfterFirstDownload { get; set; }
}

public sealed class CreateTransferFromUploadsResponse
{
    public bool Success { get; set; }
    public Guid TransferId { get; set; }
    public string DownloadToken { get; set; } = "";
    public string DownloadLink { get; set; } = "";
    public string RedirectUrl { get; set; } = "";
    public int FileCount { get; set; }
}
