using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class EntraAdminController : Controller
{
    private readonly IEntraReadinessService _readiness;
    private readonly ISettingsRepository _settings;
    private readonly IAuditRepository _audit;

    public EntraAdminController(IEntraReadinessService readiness, ISettingsRepository settings, IAuditRepository audit)
    {
        _readiness = readiness;
        _settings = settings;
        _audit = audit;
    }

    [HttpGet("/Admin/Entra")]
    public async Task<IActionResult> Index()
    {
        return View(await _readiness.GetAsync(Request));
    }

    [HttpPost("/Admin/Entra/MarkConsentGranted")]
    public async Task<IActionResult> MarkConsentGranted()
    {
        await _settings.UpdateAsync("Security.EntraAdminConsentGranted", "true");
        await _audit.WriteAsync(null, User?.Identity?.Name, "Entra Consent Marked Granted", null, HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = "Admin consent marked as granted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/Entra/EnableMicrosoftLogin")]
    public async Task<IActionResult> EnableMicrosoftLogin()
    {
        await _settings.UpdateAsync("Security.RequireMicrosoftLoginForUploads", "true");
        await _settings.UpdateAsync("Security.AllowTemporaryLocalUpload", "false");
        await _audit.WriteAsync(null, User?.Identity?.Name, "Microsoft Login Required", "Temporary local upload disabled.", HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = "Microsoft login is now required for uploads.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/Entra/EnableTemporaryUpload")]
    public async Task<IActionResult> EnableTemporaryUpload()
    {
        await _settings.UpdateAsync("Security.RequireMicrosoftLoginForUploads", "false");
        await _settings.UpdateAsync("Security.AllowTemporaryLocalUpload", "true");
        await _audit.WriteAsync(null, User?.Identity?.Name, "Temporary Local Upload Enabled", null, HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = "Temporary local upload mode is enabled.";
        return RedirectToAction(nameof(Index));
    }
}
