using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class BrandingController : Controller
{
    private readonly ISettingsRepository _settings;
    private readonly IBrandingService _branding;
    private readonly IAuditRepository _audit;
    private readonly IWebHostEnvironment _env;

    public BrandingController(
        ISettingsRepository settings,
        IBrandingService branding,
        IAuditRepository audit,
        IWebHostEnvironment env)
    {
        _settings = settings;
        _branding = branding;
        _audit = audit;
        _env = env;
    }

    [HttpGet("/Admin/Branding")]
    public async Task<IActionResult> Index()
    {
        return View(await _branding.GetAsync());
    }

    [HttpPost("/Admin/Branding/Text")]
    public async Task<IActionResult> SaveText(BrandingViewModel model)
    {
        await _settings.UpdateAsync("Branding.OrganizationName", model.OrganizationName);
        await _settings.UpdateAsync("Branding.ApplicationTitle", model.ApplicationTitle);
        await _settings.UpdateAsync("Branding.DepartmentName", model.DepartmentName);

        await _audit.WriteAsync(null, User?.Identity?.Name, "Branding Updated", "Text branding updated", HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Message"] = "Branding text saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/Branding/Logo")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UploadLogo(IFormFile logo)
    {
        if (logo is null || logo.Length == 0)
        {
            TempData["Message"] = "Choose a logo file.";
            return RedirectToAction(nameof(Index));
        }

        var ext = Path.GetExtension(logo.FileName).ToLowerInvariant();
        var allowed = new HashSet<string> { ".png", ".jpg", ".jpeg", ".webp", ".svg" };

        if (!allowed.Contains(ext))
        {
            TempData["Message"] = "Logo must be PNG, JPG, WEBP, or SVG.";
            return RedirectToAction(nameof(Index));
        }

        var logoFolder = Path.Combine(_env.WebRootPath, "img");
        Directory.CreateDirectory(logoFolder);

        var fileName = "filedrop-logo" + ext;
        var physicalPath = Path.Combine(logoFolder, fileName);

        await using (var output = System.IO.File.Create(physicalPath))
        {
            await logo.CopyToAsync(output);
        }

        var webPath = "/img/" + fileName;
        await _settings.UpdateAsync("Branding.LogoPath", webPath);

        await _audit.WriteAsync(null, User?.Identity?.Name, "Logo Uploaded", webPath, HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Message"] = "Logo uploaded.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/Branding/Logo/Remove")]
    public async Task<IActionResult> RemoveLogo()
    {
        var current = await _settings.GetValueAsync("Branding.LogoPath");

        if (!string.IsNullOrWhiteSpace(current) && current.StartsWith("/img/"))
        {
            var physical = Path.Combine(_env.WebRootPath, current.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(physical))
            {
                System.IO.File.Delete(physical);
            }
        }

        await _settings.UpdateAsync("Branding.LogoPath", "");
        await _audit.WriteAsync(null, User?.Identity?.Name, "Logo Removed", current, HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Message"] = "Logo removed.";
        return RedirectToAction(nameof(Index));
    }
}
