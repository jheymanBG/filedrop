using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class UploadManagerController : Controller
{
    private readonly IUploadManagerService _uploadManager;
    private readonly IAuditRepository _audit;

    public UploadManagerController(IUploadManagerService uploadManager, IAuditRepository audit)
    {
        _uploadManager = uploadManager;
        _audit = audit;
    }

    [HttpGet("/Admin/Uploads")]
    public async Task<IActionResult> Index() => View(await _uploadManager.GetAsync());

    [HttpPost("/Admin/Uploads/Save")]
    public async Task<IActionResult> Save(decimal maximumUploadGb, int maximumFilesPerTransfer, int requestTimeoutMinutes)
    {
        try
        {
            await _uploadManager.SaveAsync(maximumUploadGb, maximumFilesPerTransfer, requestTimeoutMinutes);
            await _audit.WriteAsync(null, User?.Identity?.Name, "Upload Configuration Saved", $"MaxGB={maximumUploadGb}", HttpContext.Connection.RemoteIpAddress?.ToString());
            TempData["Message"] = "Upload configuration saved and synced. Recycle the FileDrop app pool if needed.";
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
        await _uploadManager.RepairAsync();
        TempData["Message"] = "Upload configuration repaired.";
        return RedirectToAction(nameof(Index));
    }
}
