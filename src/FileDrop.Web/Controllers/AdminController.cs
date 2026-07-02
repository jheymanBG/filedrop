using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

// TODO: Add [Authorize] after Entra admin consent is granted.
[RequireAdminAccess]
public sealed class AdminController : Controller
{
    private readonly IAdminRepository _admin;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;
    private readonly IAuditRepository _audit;
    private readonly ICleanupService _cleanup;

    public AdminController(
        IAdminRepository admin,
        IEmailService email,
        IConfiguration config,
        IAuditRepository audit,
        ICleanupService cleanup)
    {
        _admin = admin;
        _email = email;
        _config = config;
        _audit = audit;
        _cleanup = cleanup;
    }

    [HttpGet("/Admin")]
    public async Task<IActionResult> Index()
    {
        return View(await _admin.GetDashboardAsync());
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
    public async Task<IActionResult> Audit(string? query)
    {
        return View(new AdminAuditViewModel
        {
            Query = query,
            Records = await _audit.SearchAsync(query)
        });
    }

    [HttpPost("/Admin/CleanupExpired")]
    public async Task<IActionResult> CleanupExpired(int olderThanDays = 0)
    {
        var count = await _cleanup.DeleteExpiredTransfersAsync(olderThanDays);
        await _audit.WriteAsync(null, User?.Identity?.Name, "Cleanup Expired", $"DeletedTransfers={count}; OlderThanDays={olderThanDays}", HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = $"Deleted {count} expired transfer(s).";
        return RedirectToAction(nameof(Index));
    }
}

