using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;
using System.Security.Claims;

namespace FileDrop.Web.Services;

public interface IEntraValidationService
{
    Task<EntraValidationViewModel> ValidateAsync(HttpContext context);
}

public sealed class EntraValidationService : IEntraValidationService
{
    private readonly IConfiguration _config;
    private readonly ISettingsRepository _settings;

    public EntraValidationService(IConfiguration config, ISettingsRepository settings)
    {
        _config = config;
        _settings = settings;
    }

    public async Task<EntraValidationViewModel> ValidateAsync(HttpContext context)
    {
        var model = new EntraValidationViewModel();
        var request = context.Request;

        var tenantId = _config["AzureAd:TenantId"] ?? "";
        var clientId = _config["AzureAd:ClientId"] ?? "";
        var domain = _config["AzureAd:Domain"] ?? "";
        var callbackPath = _config["AzureAd:CallbackPath"] ?? "";
        var instance = _config["AzureAd:Instance"] ?? "";
        var lastLogin = await _settings.GetValueAsync("Security.LastSuccessfulMicrosoftLogin");
        model.LastSuccessfulMicrosoftLogin = lastLogin;

        Add(model, "Configuration", "Tenant ID", IsGuid(tenantId) ? "Pass" : "Fail", tenantId);
        Add(model, "Configuration", "Client ID", IsGuid(clientId) ? "Pass" : "Fail", clientId);
        Add(model, "Configuration", "Domain", string.IsNullOrWhiteSpace(domain) ? "Warning" : "Pass", domain);
        Add(model, "Configuration", "Authority Instance", instance.StartsWith("https://login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ? "Pass" : "Warning", instance);
        Add(model, "Configuration", "Callback Path", callbackPath.Equals("/signin-oidc", StringComparison.OrdinalIgnoreCase) ? "Pass" : "Warning", callbackPath);

        var localRedirect = $"{request.Scheme}://{request.Host}{callbackPath}";
        Add(model, "Redirect URI", "Current Redirect URI", localRedirect.Contains(callbackPath) ? "Pass" : "Fail", localRedirect);
        Add(model, "Redirect URI", "Production Redirect URI", "Warning", "Confirm this exists in Azure: https://filedrop.bgohio.gov/signin-oidc");

        var signedIn = context.User?.Identity?.IsAuthenticated == true;
        Add(model, "Authentication", "Current Session Signed In", signedIn ? "Pass" : "Warning", signedIn ? context.User?.Identity?.Name ?? "Signed in" : "Not signed in with Microsoft in this session");

        var email = context.User?.FindFirstValue("preferred_username") ?? context.User?.FindFirstValue(ClaimTypes.Email);
        Add(model, "Authentication", "User Email Claim", string.IsNullOrWhiteSpace(email) ? "Warning" : "Pass", email ?? "No email claim found");

        var hasGroups = context.User?.Claims.Any(c => c.Type.Equals("groups", StringComparison.OrdinalIgnoreCase) || c.Type.Contains("/groups", StringComparison.OrdinalIgnoreCase)) == true;
        Add(model, "Authorization", "Group Claims Present", hasGroups ? "Pass" : "Warning", hasGroups ? "Group claims found in token" : "No group claims found. Add group claims to token or use Graph later.");

        var adminGroup = await _settings.GetValueAsync("Authorization.AdminGroupObjectId") ?? "";
        Add(model, "Authorization", "Admin Group Object ID Configured", string.IsNullOrWhiteSpace(adminGroup) ? "Warning" : "Pass", string.IsNullOrWhiteSpace(adminGroup) ? "Not configured yet" : adminGroup);

        var requireMicrosoftLogin = (await _settings.GetValueAsync("Security.RequireMicrosoftLoginForUploads"))?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        var tempUpload = (await _settings.GetValueAsync("Security.AllowTemporaryLocalUpload"))?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        Add(model, "FileDrop Mode", "Microsoft Login Required", requireMicrosoftLogin ? "Pass" : "Warning", requireMicrosoftLogin ? "Enabled" : "Temporary local upload still enabled");
        Add(model, "FileDrop Mode", "Temporary Local Upload", tempUpload ? "Warning" : "Pass", tempUpload ? "Enabled" : "Disabled");

        var sqlOk = await CanConnectSqlAsync();
        Add(model, "Infrastructure", "SQL Connection", sqlOk ? "Pass" : "Fail", sqlOk ? "Connected" : "Could not connect");

        var storageRoot = _config["Storage:RootPath"] ?? "C:\\SecureFileTransfer";
        Add(model, "Infrastructure", "Storage Root", Directory.Exists(storageRoot) ? "Pass" : "Fail", storageRoot);

        var smtp = _config["Email:SmtpServer"];
        Add(model, "Infrastructure", "SMTP Server", string.IsNullOrWhiteSpace(smtp) ? "Warning" : "Pass", smtp ?? "Missing");

        var defender = FindDefenderPath();
        Add(model, "Security", "Microsoft Defender Scanner", string.IsNullOrWhiteSpace(defender) ? "Warning" : "Pass", defender ?? "MpCmdRun.exe not found");

        var hasSuccessfulLogin = !string.IsNullOrWhiteSpace(lastLogin) || signedIn;
        model.CanSwitchToMicrosoftLogin =
            IsGuid(tenantId) &&
            IsGuid(clientId) &&
            callbackPath.Equals("/signin-oidc", StringComparison.OrdinalIgnoreCase) &&
            hasSuccessfulLogin;

        Add(model, "Decision", "Ready To Require Microsoft Login", model.CanSwitchToMicrosoftLogin ? "Pass" : "Warning", model.CanSwitchToMicrosoftLogin ? "Yes" : "Need successful Microsoft sign-in first");

        return model;
    }

    private async Task<bool> CanConnectSqlAsync()
    {
        try
        {
            var cs = _config.GetConnectionString("DefaultConnection");
            await using var db = new SqlConnection(cs);
            await db.OpenAsync();
            return true;
        }
        catch { return false; }
    }

    private static bool IsGuid(string value) => Guid.TryParse(value, out _);

    private static void Add(EntraValidationViewModel model, string category, string name, string status, string details)
    {
        model.Items.Add(new EntraValidationItem { Category = category, Name = name, Status = status, Details = details });
    }

    private static string? FindDefenderPath()
    {
        var first = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        if (File.Exists(first)) return first;

        var platform = @"C:\ProgramData\Microsoft\Windows Defender\Platform";
        if (!Directory.Exists(platform)) return null;

        return Directory.GetDirectories(platform)
            .OrderByDescending(x => x)
            .Select(x => Path.Combine(x, "MpCmdRun.exe"))
            .FirstOrDefault(File.Exists);
    }
}
