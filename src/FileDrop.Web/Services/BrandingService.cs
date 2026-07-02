using FileDrop.Web.Models;

namespace FileDrop.Web.Services;

public interface IBrandingService
{
    Task<BrandingViewModel> GetAsync();
}

public sealed class BrandingService : IBrandingService
{
    private readonly ISettingsRepository _settings;

    public BrandingService(ISettingsRepository settings)
    {
        _settings = settings;
    }

    public async Task<BrandingViewModel> GetAsync()
    {
        return new BrandingViewModel
        {
            OrganizationName = await _settings.GetValueAsync("Branding.OrganizationName") ?? "City of Bowling Green",
            ApplicationTitle = await _settings.GetValueAsync("Branding.ApplicationTitle") ?? "Secure File Transfer",
            DepartmentName = await _settings.GetValueAsync("Branding.DepartmentName") ?? "GIS Department",
            LogoPath = await _settings.GetValueAsync("Branding.LogoPath")
        };
    }
}
