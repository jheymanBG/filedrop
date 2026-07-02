using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

// TODO: Add [Authorize] after Entra admin consent is granted.
[RequireAdminAccess]
public sealed class SettingsController : Controller
{
    private readonly ISettingsRepository _settings;
    private readonly IAuditRepository _audit;
    private readonly IHealthService _health;
    private readonly ICleanupService _cleanup;

    public SettingsController(
        ISettingsRepository settings,
        IAuditRepository audit,
        IHealthService health,
        ICleanupService cleanup)
    {
        _settings = settings;
        _audit = audit;
        _health = health;
        _cleanup = cleanup;
    }

    [HttpGet("/Admin/Settings")]
    public async Task<IActionResult> Index()
    {
        return View(new FileDrop.Web.Models.SettingsViewModel
        {
            Settings = await _settings.GetAllAsync()
        });
    }

    [HttpPost("/Admin/Settings")]
    public async Task<IActionResult> Save(string key, string? value)
    {
        await _settings.UpdateAsync(key, value);
        await _audit.WriteAsync(null, User?.Identity?.Name, "Setting Updated", $"{key}={value}", HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Message"] = "Setting saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("/Admin/Health")]
    public async Task<IActionResult> Health()
    {
        return View(await _health.CheckAsync());
    }

    [HttpPost("/Admin/RunCleanup")]
    public async Task<IActionResult> RunCleanup()
    {
        var olderThanDays = await _settings.GetIntAsync("Cleanup.DeleteExpiredOlderThanDays", 0);
        var count = await _cleanup.DeleteExpiredTransfersAsync(olderThanDays);

        await _audit.WriteAsync(null, User?.Identity?.Name, "Scheduled Cleanup Manual Run", $"DeletedTransfers={count}; OlderThanDays={olderThanDays}", HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Message"] = $"Cleanup complete. Deleted {count} expired transfer(s).";
        return RedirectToAction(nameof(Health));
    }
}

