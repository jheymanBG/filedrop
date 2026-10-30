namespace FileDrop.Web.Models;

public sealed class PiiReviewRequestRecord
{
    public long ReviewId { get; set; }
    public Guid UploadId { get; set; }
    public Guid? TransferId { get; set; }
    public long PiiScanId { get; set; }
    public string RequestedBy { get; set; } = "";
    public string? RecipientEmail { get; set; }
    public bool Acknowledged { get; set; }
    public string Justification { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public DateTime RequestedUtc { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedUtc { get; set; }
    public string? DecisionReason { get; set; }
}

public sealed class PiiReviewSubmission
{
    public Guid UploadId { get; set; }
    public bool Acknowledged { get; set; }
    public string Justification { get; set; } = "";
    public string RecipientEmail { get; set; } = "";
}

public sealed class PiiReviewUploadViewModel
{
    public Guid UploadId { get; set; }
    public PiiScanRecord Scan { get; set; } = new();
    public IReadOnlyList<PiiScanFindingRecord> Findings { get; set; } = Array.Empty<PiiScanFindingRecord>();
}

public sealed class PiiScanFindingRecord
{
    public long FindingId { get; set; }
    public long PiiScanId { get; set; }
    public string Category { get; set; } = "";
    public string Confidence { get; set; } = "";
    public int? PageNumber { get; set; }
    public string? MaskedEvidence { get; set; }
    public string? DetectionReason { get; set; }
}
