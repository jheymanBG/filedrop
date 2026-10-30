namespace FileDrop.Web.Models;

public sealed class PiiFindingSummary
{
    public string Category { get; set; } = "";
    public int Count { get; set; }
    public string Severity { get; set; } = "Low";
    public string Confidence { get; set; } = "Low";
    public string Location { get; set; } = "Document";
    public string RedactedValue { get; set; } = "";
    public string Context { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class PiiScanResult
{
    public string Provider { get; set; } = "BuiltIn";
    public string Result { get; set; } = "Unknown";
    public bool Allowed { get; set; }
    public bool RequiresAcknowledgement { get; set; }
    public bool HasHighConfidence { get; set; }
    public string MaxConfidence { get; set; } = "None";
    public int FindingCount { get; set; }
    public List<PiiFindingSummary> Findings { get; set; } = new();
    public string Message { get; set; } = "";
    public int ExtractedCharacters { get; set; }
    public int UnsupportedItems { get; set; }
    public string Categories => string.Join(", ", Findings.Select(x => x.Category).Distinct(StringComparer.OrdinalIgnoreCase));
}

public sealed class PiiScanRecord
{
    public long PiiScanId { get; set; }
    public Guid UploadId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string Result { get; set; } = "Unknown";
    public bool Allowed { get; set; }
    public int FindingCount { get; set; }
    public string? Categories { get; set; }
    public string? Details { get; set; }
    public DateTime ScannedDate { get; set; }
}
