namespace FileDrop.Web.Models;

public sealed class UploadPreflightRequest
{
    public List<UploadPreflightFileItem> Files { get; set; } = new();
}

public sealed class UploadPreflightFileItem
{
    public string FileName { get; set; } = "";
    public string? ContentType { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class UploadPreflightResponse
{
    public bool Allowed { get; set; }
    public bool AcceptingUploads { get; set; }
    public string Title { get; set; } = "Upload Check";
    public string Message { get; set; } = "";
    public string ContactDepartment { get; set; } = "GIS Department";
    public string ContactEmail { get; set; } = "gisstaff@bgohio.gov";
    public long TotalSelectedBytes { get; set; }
    public long AvailableBytes { get; set; }
    public long RequiredBytes { get; set; }
    public string StorageRoot { get; set; } = "";
    public List<UploadPreflightFileResult> Files { get; set; } = new();
}

public sealed class UploadPreflightFileResult
{
    public string FileName { get; set; } = "";
    public long TotalBytes { get; set; }
    public bool Allowed { get; set; }
    public string Status { get; set; } = "Pending";
    public string Message { get; set; } = "";
}

public sealed class ScanCompletedUploadRequest
{
    public Guid UploadId { get; set; }
}

public sealed class PiiAcknowledgementRequest
{
    public Guid UploadId { get; set; }
    public bool Accepted { get; set; }
}

public sealed class ScanCompletedUploadResponse
{
    public Guid UploadId { get; set; }
    public bool Allowed { get; set; }
    public bool RequiresAcknowledgement { get; set; }
    public string Status { get; set; } = "Unknown";
    public string Message { get; set; } = "";
    public string? ThreatName { get; set; }
    public string? Details { get; set; }
    public string MalwareStatus { get; set; } = "NotRun";
    public string PiiStatus { get; set; } = "NotRun";
    public bool PiiDetected { get; set; }
    public int PiiFindingCount { get; set; }
    public string PiiMaxConfidence { get; set; } = "None";
    public string[] PiiCategories { get; set; } = Array.Empty<string>();
    public List<PiiFindingSummary> PiiFindings { get; set; } = new();
}
