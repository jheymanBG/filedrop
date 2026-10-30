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
    public string Engine { get; set; } = "Unknown";
    public string Result { get; set; } = "Unknown";
    public string? ThreatName { get; set; }
    public string? Details { get; set; }

    public bool IsClean => Result.Equals("Clean", StringComparison.OrdinalIgnoreCase);
    public bool IsProtected => Result.Equals("Protected", StringComparison.OrdinalIgnoreCase);
    public bool IsThreat => Result.Equals("Infected", StringComparison.OrdinalIgnoreCase) || Result.Equals("Quarantined", StringComparison.OrdinalIgnoreCase);
    public bool IsUnavailable => Result.Equals("Unavailable", StringComparison.OrdinalIgnoreCase) || Result.Equals("Failed", StringComparison.OrdinalIgnoreCase);
    public bool IsDisabled => Result.Equals("Disabled", StringComparison.OrdinalIgnoreCase);

    // A clean on-demand scan, active endpoint protection, or an explicitly disabled
    // provider are all non-blocking states. Scanner failures are handled separately by policy.
    public bool IsAcceptedProtection => IsClean || IsProtected || IsDisabled;
}
