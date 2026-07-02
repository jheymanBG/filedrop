namespace FileDrop.Web.Models;

public sealed class FileScanRecord
{
    public long ScanId { get; set; }
    public Guid? TransferId { get; set; }
    public Guid? FileId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string? StoragePath { get; set; }
    public string? Sha256Hash { get; set; }
    public string Engine { get; set; } = "";
    public string Result { get; set; } = "";
    public string? ThreatName { get; set; }
    public string? Details { get; set; }
    public DateTime ScannedDate { get; set; }
}

public sealed class SecurityDashboardViewModel
{
    public int TotalScans { get; set; }
    public int CleanCount { get; set; }
    public int QuarantinedCount { get; set; }
    public int FailedCount { get; set; }
    public int InfectedCount { get; set; }
    public List<FileScanRecord> RecentScans { get; set; } = new();
}

public sealed class ScanResult
{
    public string Engine { get; set; } = "Microsoft Defender";
    public string Result { get; set; } = "Unknown";
    public string? ThreatName { get; set; }
    public string? Details { get; set; }
    public bool IsClean => Result.Equals("Clean", StringComparison.OrdinalIgnoreCase);
}
