using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class StorageAdminController : Controller
{
    private readonly IStorageLocationService _storage;
    private readonly ISettingsRepository _settings;
    private readonly IAuditRepository _audit;

    public StorageAdminController(IStorageLocationService storage, ISettingsRepository settings, IAuditRepository audit)
    {
        _storage = storage;
        _settings = settings;
        _audit = audit;
    }

    [HttpGet("/Admin/Storage")]
    public async Task<IActionResult> Index() => View(await _storage.GetAdminModelAsync());

    [HttpPost("/Admin/Storage/Test")]
    public async Task<IActionResult> Test([FromBody] StoragePathTestRequest request, CancellationToken cancellationToken)
        => Json(await _storage.TestPathAsync(request.RootPath, cancellationToken));

    [HttpPost("/Admin/Storage/Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string rootPath, bool readOnly, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.SaveActiveLocationAsync(rootPath, readOnly, cancellationToken);
            await _audit.WriteAsync(null, User?.Identity?.Name, "Active Storage Changed", $"RootPath={rootPath}; ReadOnly={readOnly}", HttpContext.Connection.RemoteIpAddress?.ToString());
            TempData["Message"] = "Active storage location saved. Existing uploads continue using their recorded paths; new uploads use this location.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/Storage/DriveAlerts")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDriveAlerts(string[]? enabledDrives)
    {
        var enabled = new HashSet<string>((enabledDrives ?? Array.Empty<string>()).Select(StorageLocationService.NormalizeDriveName), StringComparer.OrdinalIgnoreCase);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not DriveType.Fixed and not DriveType.Network)
            {
                continue;
            }

            var driveName = StorageLocationService.NormalizeDriveName(drive.Name);
            await _settings.UpsertAsync(
                StorageLocationService.DriveAlertSettingKey(driveName),
                enabled.Contains(driveName).ToString(),
                $"Enable low disk space email alerts for drive {driveName}.");
        }

        await _audit.WriteAsync(null, User?.Identity?.Name, "Drive Alert Settings Updated", $"EnabledDrives={string.Join(",", enabled.OrderBy(x => x))}", HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = "Drive warning settings saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/Storage/Protection")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveProtection(bool enabled, int minimumFreeSpaceGb, int safetyBufferGb, string contactDepartment, string contactEmail)
    {
        minimumFreeSpaceGb = Math.Max(1, minimumFreeSpaceGb);
        safetyBufferGb = Math.Max(0, safetyBufferGb);

        await _settings.UpsertAsync("Storage.Protection.Enabled", enabled.ToString(), "Enable preflight storage protection.");
        await _settings.UpsertAsync("Storage.MinimumFreeSpaceBytes", ((long)minimumFreeSpaceGb * 1024 * 1024 * 1024).ToString(), "Minimum free space reserved by FileDrop.");
        await _settings.UpsertAsync("Storage.SafetyBufferBytes", ((long)safetyBufferGb * 1024 * 1024 * 1024).ToString(), "Additional free-space buffer required for uploads.");
        await _settings.UpsertAsync("Storage.ContactDepartment", contactDepartment?.Trim(), "Department shown when uploads are unavailable.");
        await _settings.UpsertAsync("Storage.ContactEmail", contactEmail?.Trim(), "Contact email shown when uploads are unavailable.");

        await _audit.WriteAsync(null, User?.Identity?.Name, "Storage Protection Updated", $"Enabled={enabled}; MinimumGB={minimumFreeSpaceGb}; BufferGB={safetyBufferGb}; Contact={contactEmail}", HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = "Storage protection settings saved.";
        return RedirectToAction(nameof(Index));
    }
}
