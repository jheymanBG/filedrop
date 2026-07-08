namespace FileDrop.Web.Models;

public sealed class UploadManagerViewModel
{
    public long MaximumUploadBytes { get; set; }
    public decimal MaximumUploadGB { get; set; }
    public int MaximumFilesPerTransfer { get; set; }
    public int RequestTimeoutMinutes { get; set; }
    public int RetentionDays { get; set; }
    public bool AutomaticCleanupEnabled { get; set; }
    public int AutomaticCleanupHourUtc { get; set; }
    public long EffectiveWebConfigBytes { get; set; }
    public string PublishWebConfigPath { get; set; } = "";
    public bool PublishWebConfigExists { get; set; }
    public bool IsSynced => EffectiveWebConfigBytes == MaximumUploadBytes;

    public UploadManagerDashboard Dashboard { get; set; } = new();
}

public sealed class UploadManagerDashboard
{
    public int ActiveUploadCount { get; set; }
    public int FinalizingUploadCount { get; set; }
    public int CompletedUploadCount24h { get; set; }
    public int FailedUploadCount24h { get; set; }
    public int AbandonedUploadCount { get; set; }
    public long ActiveBytesReceived { get; set; }
    public long ActiveTotalBytes { get; set; }
    public long CompletedBytes24h { get; set; }
    public DateTime? OldestActiveDate { get; set; }
    public DateTime? LastCompletedDate { get; set; }
    public List<UploadManagerActiveUploadRow> ActiveUploads { get; set; } = new();
    public List<UploadManagerRecentUploadRow> RecentUploads { get; set; } = new();
    public TransferStorageSummary Storage { get; set; } = new();
    public List<TransferStorageBySenderRow> TopSenders { get; set; } = new();

    public decimal ActivePercent => ActiveTotalBytes <= 0 ? 0 : Math.Round(ActiveBytesReceived / (decimal)ActiveTotalBytes * 100, 1);
}

public sealed class UploadManagerActiveUploadRow
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

public sealed class UploadManagerRecentUploadRow
{
    public Guid UploadId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string? CreatedByEmail { get; set; }
    public string Status { get; set; } = "";
    public long TotalBytes { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public DateTime LastActivityDate { get; set; }
    public string? Sha256Hash { get; set; }
}

public sealed class TransferStorageSummary
{
    public int ActiveTransferCount { get; set; }
    public int ExpiredTransferCount { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public long ExpiredBytes { get; set; }
    public long BytesCreated24h { get; set; }
    public DateTime? OldestExpirationDate { get; set; }
    public DateTime? NewestTransferDate { get; set; }
}

public sealed class TransferStorageBySenderRow
{
    public string SenderEmail { get; set; } = "";
    public int TransferCount { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public DateTime? LastTransferDate { get; set; }
}
