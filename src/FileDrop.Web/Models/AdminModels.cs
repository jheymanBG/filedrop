namespace FileDrop.Web.Models;

public sealed class AdminDashboardViewModel
{
    public int ActiveTransfers { get; set; }
    public int ExpiredTransfers { get; set; }
    public int TotalTransfers { get; set; }
    public int TotalFiles { get; set; }
    public long TotalStorageBytes { get; set; }
    public int DownloadsTotal { get; set; }
    public List<AdminTransferSummary> RecentTransfers { get; set; } = new();
    public List<AdminFileSummary> LargestFiles { get; set; } = new();
}

public sealed class AdminTransferSummary
{
    public Guid TransferId { get; set; }
    public string SenderEmail { get; set; } = "";
    public string? SenderName { get; set; }
    public string RecipientEmail { get; set; } = "";
    public string? Subject { get; set; }
    public string DownloadToken { get; set; } = "";
    public DateTime CreatedDate { get; set; }
    public DateTime ExpirationDate { get; set; }
    public int DownloadCount { get; set; }
    public string Status { get; set; } = "";
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class AdminFileSummary
{
    public Guid FileId { get; set; }
    public Guid TransferId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string StoragePath { get; set; } = "";
    public string RecipientEmail { get; set; } = "";
    public DateTime UploadedDate { get; set; }
}

public sealed class AdminTransferDetailViewModel
{
    public TransferRecord Transfer { get; set; } = new();
    public List<TransferFileRecord> Files { get; set; } = new();
}

public sealed class AdminTransferSearchViewModel
{
    public string? Query { get; set; }
    public string? Status { get; set; }
    public List<AdminTransferSummary> Results { get; set; } = new();
}
