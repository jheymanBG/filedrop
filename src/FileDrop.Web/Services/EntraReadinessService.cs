using FileDrop.Web.Models;

namespace FileDrop.Web.Services;

public interface IEntraReadinessService
{
    Task<EntraReadinessViewModel> GetAsync(HttpRequest request);
}

public sealed class EntraReadinessService : IEntraReadinessService
{
    private readonly IConfiguration _config;
    private readonly ISettingsRepository _settings;

    public EntraReadinessService(IConfiguration config, ISettingsRepository settings)
    {
        _config = config;
        _settings = settings;
    }

    public async Task<EntraReadinessViewModel> GetAsync(HttpRequest request)
    {
        var callback = _config["AzureAd:CallbackPath"] ?? "/signin-oidc";
        var host = request.Host.HasValue ? request.Host.Value : "localhost:8080";

        return new EntraReadinessViewModel
        {
            TenantId = _config["AzureAd:TenantId"] ?? "",
            ClientId = _config["AzureAd:ClientId"] ?? "",
            Domain = _config["AzureAd:Domain"] ?? "",
            CallbackPath = callback,
            LocalRedirectUri = $"http://{host}{callback}",
            ProductionRedirectUri = "https://filedrop.bgohio.gov" + callback,
            AdminConsentGranted = (await _settings.GetValueAsync("Security.EntraAdminConsentGranted"))?.Equals("true", StringComparison.OrdinalIgnoreCase) == true,
            RequireMicrosoftLoginForUploads = (await _settings.GetValueAsync("Security.RequireMicrosoftLoginForUploads"))?.Equals("true", StringComparison.OrdinalIgnoreCase) == true,
            AllowTemporaryLocalUpload = (await _settings.GetValueAsync("Security.AllowTemporaryLocalUpload"))?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
        };
    }
}
