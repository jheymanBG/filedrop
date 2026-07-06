using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class AuthorizationAdminController : Controller
{
    private readonly IEnterpriseAuthorizationService _authorization;
    private readonly ISettingsRepository _settings;
    private readonly IAuditRepository _audit;

    public AuthorizationAdminController(
        IEnterpriseAuthorizationService authorization,
        ISettingsRepository settings,
        IAuditRepository audit)
    {
        _authorization = authorization;
        _settings = settings;
        _audit = audit;
    }

    [HttpGet("/Admin/Authorization")]
    public async Task<IActionResult> Index()
    {
        return View(await _authorization.GetStatusAsync(HttpContext));
    }

    [HttpPost("/Admin/Authorization/Save")]
    public async Task<IActionResult> Save(
        string mode,
        string adminGroupObjectId,
        string auditorGroupObjectId,
        string helpDeskGroupObjectId,
        bool requireAdminGroupForAdminPortal)
    {
        await _settings.UpdateAsync("Authorization.Mode", mode);
        await _settings.UpdateAsync("Authorization.AdminGroupObjectId", adminGroupObjectId);
        await _settings.UpdateAsync("Authorization.AuditorGroupObjectId", auditorGroupObjectId);
        await _settings.UpdateAsync("Authorization.HelpDeskGroupObjectId", helpDeskGroupObjectId);
        await _settings.UpdateAsync("Authorization.RequireAdminGroupForAdminPortal", requireAdminGroupForAdminPortal ? "true" : "false");

        await _audit.WriteAsync(null, User?.Identity?.Name, "Authorization Settings Updated", $"Mode={mode}", HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Message"] = "Authorization settings saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("/Admin/AccessDenied")]
    public IActionResult AccessDenied(string? role)
    {
        ViewBag.Role = role;
        return View();
    }
}
