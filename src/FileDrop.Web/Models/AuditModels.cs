namespace FileDrop.Web.Models;

public sealed class AuditRecord
{
    public long AuditId { get; set; }
    public Guid? TransferId { get; set; }
    public string? UserEmail { get; set; }
    public string Action { get; set; } = "";
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedDate { get; set; }
}

public sealed class AdminAuditViewModel
{
    public string? Query { get; set; }
    public List<AuditRecord> Records { get; set; } = new();
}
