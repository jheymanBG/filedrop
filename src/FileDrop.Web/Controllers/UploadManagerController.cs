using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class UploadManagerController : Controller
{
    private readonly IUploadManagerService _uploadManager;
    private readonly IChunkedUploadService _chunkedUploads;
    private readonly ICleanupService _cleanup;
    private readonly IAuditRepository _audit;

    public UploadManagerController(IUploadManagerService uploadManager, IChunkedUploadService chunkedUploads, ICleanupService cleanup, IAuditRepository audit)
    {
        _uploadManager = uploadManager;
        _chunkedUploads = chunkedUploads;
        _cleanup = cleanup;
        _audit = audit;
    }

    [HttpGet("/Admin/Uploads")]
    public async Task<IActionResult> Index() => View(await _uploadManager.GetAsync());

    [HttpGet("/Admin/Uploads/Live")]
    public async Task<IActionResult> Live()
    {
        return Json(await _uploadManager.GetDashboardAsync());
    }

    [HttpPost("/Admin/Uploads/Save")]
    public async Task<IActionResult> Save(decimal maximumUploadGb, int maximumFilesPerTransfer, int requestTimeoutMinutes)
    {
        try
        {
            await _uploadManager.SaveAsync(maximumUploadGb, maximumFilesPerTransfer, requestTimeoutMinutes);
            await _audit.WriteAsync(null, User?.Identity?.Name, "Upload Settings Saved", $"MaxGB={maximumUploadGb}; MaxFiles={maximumFilesPerTransfer}; TimeoutMinutes={requestTimeoutMinutes}; LiveWebConfigModified=False", HttpContext.Connection.RemoteIpAddress?.ToString());
            TempData["Message"] = "Upload settings saved. FileDrop did not modify the live IIS web.config.";
        }
        catch (Exception ex)
        {
            TempData["Message"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/Uploads/Retention")]
    public async Task<IActionResult> Retention(int retentionDays, bool automaticCleanupEnabled, int automaticCleanupHourUtc)
    {
        try
        {
            await _uploadManager.SaveRetentionAsync(retentionDays, automaticCleanupEnabled, automaticCleanupHourUtc);
            await _audit.WriteAsync(null, User?.Identity?.Name, "Retention Configuration Saved", $"RetentionDays={retentionDays}; AutomaticCleanupEnabled={automaticCleanupEnabled}; CleanupHourUtc={automaticCleanupHourUtc}", HttpContext.Connection.RemoteIpAddress?.ToString());
            TempData["Message"] = "Retention policy saved.";
        }
        catch (Exception ex)
        {
            TempData["Message"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/Uploads/Repair")]
    public async Task<IActionResult> Repair()
    {
        await _audit.WriteAsync(null, User?.Identity?.Name, "Legacy Upload Sync Blocked", "The retired web.config sync action was requested. No IIS configuration was modified.", HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = "Live IIS synchronization has been retired. FileDrop uses chunked uploads and does not modify web.config from the Admin page.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/Uploads/CleanupAbandoned")]
    public async Task<IActionResult> CleanupAbandoned(int olderThanHours = 24)
    {
        if (olderThanHours < 1) olderThanHours = 24;
        if (olderThanHours > 720) olderThanHours = 720;

        var count = await _chunkedUploads.CleanupAbandonedAsync(TimeSpan.FromHours(olderThanHours), HttpContext.RequestAborted);
        await _audit.WriteAsync(null, User?.Identity?.Name, "Abandoned Upload Cleanup", $"OlderThanHours={olderThanHours}; Count={count}", HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = $"Cleaned up {count} abandoned upload session(s).";
        return RedirectToAction(nameof(Index));
    }


    [HttpPost("/Admin/Uploads/{uploadId:guid}/Delete")]
    public async Task<IActionResult> DeleteUpload(Guid uploadId)
    {
        var count = await _cleanup.DeleteUploadSessionAndFilesAsync(uploadId);
        await _audit.WriteAsync(null, User?.Identity?.Name, "Upload Session Deleted", $"UploadId={uploadId}; Deleted={count}", HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = count == 0
            ? "Upload session was not found."
            : "Upload session, chunk records, temporary files, and final stored file were deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/Uploads/CleanupExpiredTransfers")]
    public async Task<IActionResult> CleanupExpiredTransfers(int olderThanDays = 0)
    {
        if (olderThanDays < 0) olderThanDays = 0;
        if (olderThanDays > 3650) olderThanDays = 3650;

        var preview = await _cleanup.PreviewExpiredTransfersAsync(olderThanDays);
        var count = await _cleanup.DeleteExpiredTransfersAsync(olderThanDays);
        await _audit.WriteAsync(null, User?.Identity?.Name, "Expired Transfer Cleanup", $"OlderThanDays={olderThanDays}; TransfersDeleted={count}; PreviewFiles={preview.FileCount}; PreviewBytes={preview.TotalBytes}", HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = $"Deleted {count} expired transfer(s), including up to {preview.FileCount} file record(s).";
        return RedirectToAction(nameof(Index));
    }
}
