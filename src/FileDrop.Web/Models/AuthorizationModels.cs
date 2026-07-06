using System.Security.Claims;

namespace FileDrop.Web.Models;

public static class FileDropRoles
{
    public const string Administrator = "Administrator";
    public const string Auditor = "Auditor";
    public const string HelpDesk = "HelpDesk";
    public const string User = "User";
}

public sealed class AuthorizationStatusViewModel
{
    public string Mode { get; set; } = "";
    public string AdminGroupObjectId { get; set; } = "";
    public string AuditorGroupObjectId { get; set; } = "";
    public string HelpDeskGroupObjectId { get; set; } = "";
    public bool RequireAdminGroupForAdminPortal { get; set; }
    public bool IsSignedIn { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
    public List<string> GroupClaims { get; set; } = new();
    public List<string> EffectiveRoles { get; set; } = new();
    public List<AuthorizationEventRecord> RecentEvents { get; set; } = new();
}

public sealed class AuthorizationEventRecord
{
    public long AuthorizationEventId { get; set; }
    public string? UserEmail { get; set; }
    public string? UserName { get; set; }
    public string? RequiredRole { get; set; }
    public bool Authorized { get; set; }
    public string? Reason { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedDate { get; set; }
}
