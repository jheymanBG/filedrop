using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

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
        return View(new SettingsViewModel
        {
            Settings = await _settings.GetAllAsync(),
            EnableLoginFailureAlerts = await _settings.GetBoolAsync("Security.EnableLoginFailureAlerts", true),
            LoginFailureAlertEmail = await _settings.GetStringAsync("Security.LoginFailureAlertEmail", "jheyman@bgohio.gov"),
            LoginFailureAlertThreshold = Math.Max(1, await _settings.GetIntAsync("Security.LoginFailureAlertThreshold", 3)),
            LoginFailureWindowMinutes = Math.Max(1, await _settings.GetIntAsync("Security.LoginFailureWindowMinutes", 15)),
            LoginFailureAlertCooldownMinutes = Math.Max(1, await _settings.GetIntAsync("Security.LoginFailureAlertCooldownMinutes", 30)),
            EnableLowDiskSpaceAlerts = await _settings.GetBoolAsync("Storage.EnableLowDiskSpaceAlerts", true),
            LowDiskSpaceAlertEmails = await _settings.GetStringAsync("Storage.LowDiskSpaceAlertEmails", "jheyman@bgohio.gov"),
            LowDiskSpaceThresholdPercentFree = Math.Clamp(await _settings.GetIntAsync("Storage.LowDiskSpaceThresholdPercentFree", 10), 1, 99),
            LowDiskSpaceThresholdFreeGb = Math.Max(1, await _settings.GetIntAsync("Storage.LowDiskSpaceThresholdFreeGb", 25)),
            LowDiskSpaceCheckIntervalMinutes = Math.Max(5, await _settings.GetIntAsync("Storage.LowDiskSpaceCheckIntervalMinutes", 30)),
            LowDiskSpaceAlertCooldownMinutes = Math.Max(5, await _settings.GetIntAsync("Storage.LowDiskSpaceAlertCooldownMinutes", 120))
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

    [HttpPost("/Admin/Settings/LoginAlerts")]
    public async Task<IActionResult> SaveLoginAlerts(
        bool enableLoginFailureAlerts,
        string loginFailureAlertEmail,
        int loginFailureAlertThreshold,
        int loginFailureWindowMinutes,
        int loginFailureAlertCooldownMinutes)
    {
        loginFailureAlertThreshold = Math.Max(1, loginFailureAlertThreshold);
        loginFailureWindowMinutes = Math.Max(1, loginFailureWindowMinutes);
        loginFailureAlertCooldownMinutes = Math.Max(1, loginFailureAlertCooldownMinutes);

        await _settings.UpsertAsync("Security.EnableLoginFailureAlerts", enableLoginFailureAlerts.ToString(), "Enable repeated failed-login email alerts.");
        await _settings.UpsertAsync("Security.LoginFailureAlertEmail", loginFailureAlertEmail?.Trim(), "Email recipients for repeated failed-login alerts. Separate multiple addresses with comma or semicolon.");
        await _settings.UpsertAsync("Security.LoginFailureAlertThreshold", loginFailureAlertThreshold.ToString(), "Failed login count before alert is sent.");
        await _settings.UpsertAsync("Security.LoginFailureWindowMinutes", loginFailureWindowMinutes.ToString(), "Rolling time window for failed login counting.");
        await _settings.UpsertAsync("Security.LoginFailureAlertCooldownMinutes", loginFailureAlertCooldownMinutes.ToString(), "Minimum minutes between repeated failed-login alerts for the same source.");

        await _audit.WriteAsync(null, User?.Identity?.Name, "Login Alert Settings Updated", $"Enabled={enableLoginFailureAlerts}; Threshold={loginFailureAlertThreshold}; Window={loginFailureWindowMinutes}; Cooldown={loginFailureAlertCooldownMinutes}; Emails={loginFailureAlertEmail}", HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Message"] = "Login alert settings saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/Settings/StorageAlerts")]
    public async Task<IActionResult> SaveStorageAlerts(
        bool enableLowDiskSpaceAlerts,
        string lowDiskSpaceAlertEmails,
        int lowDiskSpaceThresholdPercentFree,
        int lowDiskSpaceThresholdFreeGb,
        int lowDiskSpaceCheckIntervalMinutes,
        int lowDiskSpaceAlertCooldownMinutes)
    {
        lowDiskSpaceThresholdPercentFree = Math.Clamp(lowDiskSpaceThresholdPercentFree, 1, 99);
        lowDiskSpaceThresholdFreeGb = Math.Max(1, lowDiskSpaceThresholdFreeGb);
        lowDiskSpaceCheckIntervalMinutes = Math.Max(5, lowDiskSpaceCheckIntervalMinutes);
        lowDiskSpaceAlertCooldownMinutes = Math.Max(5, lowDiskSpaceAlertCooldownMinutes);

        await _settings.UpsertAsync("Storage.EnableLowDiskSpaceAlerts", enableLowDiskSpaceAlerts.ToString(), "Enable low disk space email alerts.");
        await _settings.UpsertAsync("Storage.LowDiskSpaceAlertEmails", lowDiskSpaceAlertEmails?.Trim(), "Email recipients for low disk space alerts. Separate multiple addresses with comma or semicolon.");
        await _settings.UpsertAsync("Storage.LowDiskSpaceThresholdPercentFree", lowDiskSpaceThresholdPercentFree.ToString(), "Alert when free disk space percentage is at or below this value.");
        await _settings.UpsertAsync("Storage.LowDiskSpaceThresholdFreeGb", lowDiskSpaceThresholdFreeGb.ToString(), "Alert when free disk space in GB is at or below this value.");
        await _settings.UpsertAsync("Storage.LowDiskSpaceCheckIntervalMinutes", lowDiskSpaceCheckIntervalMinutes.ToString(), "How often FileDrop checks disk space.");
        await _settings.UpsertAsync("Storage.LowDiskSpaceAlertCooldownMinutes", lowDiskSpaceAlertCooldownMinutes.ToString(), "Minimum minutes between low disk space alert emails.");

        await _audit.WriteAsync(null, User?.Identity?.Name, "Storage Alert Settings Updated", $"Enabled={enableLowDiskSpaceAlerts}; Percent={lowDiskSpaceThresholdPercentFree}; FreeGB={lowDiskSpaceThresholdFreeGb}; Interval={lowDiskSpaceCheckIntervalMinutes}; Cooldown={lowDiskSpaceAlertCooldownMinutes}; Emails={lowDiskSpaceAlertEmails}", HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Message"] = "Storage alert settings saved.";
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
