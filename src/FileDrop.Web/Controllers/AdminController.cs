using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class AdminController : Controller
{
    private readonly IAdminRepository _admin;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;
    private readonly IAuditRepository _audit;
    private readonly ICleanupService _cleanup;
    private readonly IStorageProtectionService _storageProtection;

    public AdminController(
        IAdminRepository admin,
        IEmailService email,
        IConfiguration config,
        IAuditRepository audit,
        ICleanupService cleanup,
        IStorageProtectionService storageProtection)
    {
        _admin = admin;
        _email = email;
        _config = config;
        _audit = audit;
        _cleanup = cleanup;
        _storageProtection = storageProtection;
    }

    [HttpGet("/Admin")]
    public async Task<IActionResult> Index()
    {
        var model = await _admin.GetDashboardAsync();
        await ApplyStorageStatusAsync(model);
        return View(model);
    }

    [HttpGet("/Admin/StorageStatus")]
    public async Task<IActionResult> StorageStatus()
    {
        var model = new AdminDashboardViewModel();
        await ApplyStorageStatusAsync(model);

        return Json(new
        {
            root = model.StorageRoot,
            drive = model.StorageDriveName,
            totalBytes = model.StorageDriveTotalBytes,
            usedBytes = model.StorageDriveUsedBytes,
            freeBytes = model.StorageDriveFreeBytes,
            freePercent = model.StorageDriveFreePercent,
            checkedAt = model.StorageCheckedAt
        });
    }

    [HttpGet("/Admin/Transfers")]
    public async Task<IActionResult> Transfers(string? query, string? status)
    {
        var model = new AdminTransferSearchViewModel
        {
            Query = query,
            Status = status,
            Results = await _admin.SearchTransfersAsync(query, status)
        };

        return View(model);
    }

    [HttpGet("/Admin/Transfer/{id:guid}")]
    public async Task<IActionResult> Transfer(Guid id)
    {
        var model = await _admin.GetTransferDetailAsync(id);
        if (model is null)
        {
            return NotFound();
        }

        return View(model);
    }

    [HttpPost("/Admin/Transfer/{id:guid}/Extend")]
    public async Task<IActionResult> Extend(Guid id, int days = 7)
    {
        await _admin.ExtendExpirationAsync(id, days);
        await _audit.WriteAsync(id, User?.Identity?.Name, "Expiration Extended", $"Days={days}", HttpContext.Connection.RemoteIpAddress?.ToString());
        return RedirectToAction(nameof(Transfer), new { id });
    }

    [HttpPost("/Admin/Transfer/{id:guid}/Disable")]
    public async Task<IActionResult> Disable(Guid id)
    {
        await _admin.DisableTransferAsync(id);
        await _audit.WriteAsync(id, User?.Identity?.Name, "Transfer Disabled", null, HttpContext.Connection.RemoteIpAddress?.ToString());
        return RedirectToAction(nameof(Transfer), new { id });
    }

    [HttpPost("/Admin/Transfer/{id:guid}/Delete")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await _cleanup.DeleteTransferFilesAndRecordAsync(id);
        await _audit.WriteAsync(id, User?.Identity?.Name, "Transfer Deleted", "Physical files and database records deleted.", HttpContext.Connection.RemoteIpAddress?.ToString());
        return RedirectToAction(nameof(Transfers));
    }

    [HttpPost("/Admin/Transfer/{id:guid}/Resend")]
    public async Task<IActionResult> Resend(Guid id)
    {
        var model = await _admin.GetTransferDetailAsync(id);
        if (model is null)
        {
            return NotFound();
        }

        var baseUrl = _config["Email:PublicBaseUrl"]?.TrimEnd('/') ?? $"{Request.Scheme}://{Request.Host}";
        var link = $"{baseUrl}/Transfer/Download/{model.Transfer.DownloadToken}";
        await _email.SendTransferCreatedAsync(model.Transfer, model.Files, link);
        await _audit.WriteAsync(id, User?.Identity?.Name, "Email Resent", model.Transfer.RecipientEmail, HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Message"] = "Notification resent.";
        return RedirectToAction(nameof(Transfer), new { id });
    }

    [HttpGet("/Admin/Audit")]
    public async Task<IActionResult> Audit(string? query, string? action, DateTime? from, DateTime? to)
    {
        return View(new AdminAuditViewModel
        {
            Query = query,
            Action = action,
            From = from,
            To = to,
            Actions = await _audit.GetActionsAsync(),
            Records = await _audit.SearchAsync(query, action, from, to)
        });
    }

    [HttpGet("/Admin/Audit/Export")]
    public async Task<IActionResult> ExportAudit(string? query, string? action, DateTime? from, DateTime? to)
    {
        var records = await _audit.SearchAsync(query, action, from, to);
        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Date,User,Action,Details,IP,TransferId");
        static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
        foreach (var r in records)
        {
            csv.AppendLine(string.Join(',', Csv(r.CreatedDate.ToString("O")), Csv(r.UserEmail), Csv(r.Action), Csv(r.Details), Csv(r.IpAddress), Csv(r.TransferId?.ToString())));
        }
        await _audit.WriteAsync(null, User?.Identity?.Name, "Audit Exported", $"Rows={records.Count}", HttpContext.Connection.RemoteIpAddress?.ToString());
        return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"FileDrop-Audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
    }

    [HttpPost("/Admin/CleanupExpired")]
    public async Task<IActionResult> CleanupExpired(int olderThanDays = 0)
    {
        var count = await _cleanup.DeleteExpiredTransfersAsync(olderThanDays);
        await _audit.WriteAsync(null, User?.Identity?.Name, "Cleanup Expired", $"DeletedTransfers={count}; OlderThanDays={olderThanDays}", HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = $"Deleted {count} expired transfer(s).";
        return RedirectToAction(nameof(Index));
    }

    private async Task ApplyStorageStatusAsync(AdminDashboardViewModel model)
    {
        var storageRoot = await _storageProtection.GetActiveRootPathAsync();

        model.StorageRoot = storageRoot;
        model.StorageCheckedAt = DateTime.Now;

        try
        {
            var fullPath = Path.GetFullPath(storageRoot);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root)) return;

            var drive = new DriveInfo(root);
            model.StorageDriveName = drive.Name;
            model.StorageDriveTotalBytes = drive.TotalSize;
            model.StorageDriveFreeBytes = drive.AvailableFreeSpace;
            model.StorageDriveUsedBytes = Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace);
            model.StorageDriveFreePercent = drive.TotalSize <= 0
                ? 0
                : Math.Round((decimal)drive.AvailableFreeSpace / drive.TotalSize * 100m, 1);
        }
        catch
        {
            model.StorageDriveName = "Unavailable";
        }
    }

}
