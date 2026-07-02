namespace FileDrop.Web.Models;

public sealed class EntraReadinessViewModel
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Domain { get; set; } = "";
    public string CallbackPath { get; set; } = "";
    public string LocalRedirectUri { get; set; } = "";
    public string ProductionRedirectUri { get; set; } = "";
    public bool AdminConsentGranted { get; set; }
    public bool RequireMicrosoftLoginForUploads { get; set; }
    public bool AllowTemporaryLocalUpload { get; set; }
    public bool IdTokensReminder { get; set; } = true;
}
