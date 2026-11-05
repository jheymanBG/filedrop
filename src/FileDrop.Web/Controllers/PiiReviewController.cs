using System.Security.Claims;
using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[Authorize]
public sealed class PiiReviewController : Controller
{
    private readonly PiiReviewRepository _reviews;
    private readonly IPiiScanRepository _scans;
    private readonly IAuditRepository _audit;
    private readonly PiiReviewNotificationService _notifications;

    public PiiReviewController(PiiReviewRepository reviews, IPiiScanRepository scans, IAuditRepository audit, PiiReviewNotificationService notifications)
    {
        _reviews = reviews;
        _scans = scans;
        _audit = audit;
        _notifications = notifications;
    }

    private string? CurrentEmail() => User.FindFirstValue(ClaimTypes.Email)
        ?? User.FindFirstValue("preferred_username") ?? User.Identity?.Name;

    private static bool IsReviewEligible(string? result) =>
        result is "Blocked" or "Failed" or "Unscannable" or "ProviderUnavailable";

    [HttpGet("/Upload/PiiReview/{uploadId:guid}")]
    public async Task<IActionResult> Review(Guid uploadId)
    {
        var email = CurrentEmail();
        if (string.IsNullOrWhiteSpace(email) || !await _reviews.IsUploadOwnedByAsync(uploadId, email))
            return NotFound();
        var scan = await _scans.GetLatestAsync(uploadId);
        if (scan is null || scan.Allowed || !IsReviewEligible(scan.Result))
            return BadRequest("This upload is not eligible for PII or inspection-failure review.");
        var pending = await _reviews.GetByUploadIdAsync(uploadId);
        ViewBag.ReviewStatus = pending?.Status;
        return View(new PiiReviewUploadViewModel { UploadId = uploadId, Scan = scan, Findings = await _reviews.GetFindingsAsync(scan.PiiScanId) });
    }

    [HttpPost("/Upload/PiiReview/Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit([FromForm] PiiReviewSubmission request)
    {
        var email = CurrentEmail();
        if (!request.Acknowledged || request.Justification?.Trim().Length < 15
            || request.Justification.Length > 2000 || string.IsNullOrWhiteSpace(email)
            || string.IsNullOrWhiteSpace(request.RecipientEmail) || request.RecipientEmail.Length > 255
            || !System.Net.Mail.MailAddress.TryCreate(request.RecipientEmail, out var parsedRecipient)
            || !string.Equals(parsedRecipient.Address, request.RecipientEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            return BadRequest("Acknowledgment and justification (15-2000 characters) are required.");
        if (!await _reviews.IsUploadOwnedByAsync(request.UploadId, email)) return NotFound();
        var scan = await _scans.GetLatestAsync(request.UploadId);
        if (scan is null || scan.Allowed || !string.Equals(scan.Result, "Blocked", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Only held uploads with blocked or incomplete PII inspections are eligible for review.");
        if (await _reviews.GetByUploadIdAsync(request.UploadId) is not null)
            return Conflict("A review request already exists for this upload.");
        long id;
        try { id = await _reviews.InsertPendingAsync(request.UploadId, scan.PiiScanId, email, request.Justification, request.RecipientEmail); }
        catch (Microsoft.Data.SqlClient.SqlException) { return Conflict("Review submission failed or already exists."); }
        if (id <= 0) return BadRequest("PII scan is no longer eligible.");
        await _audit.WriteAsync(null, email, "PII Review Requested",
            $"ReviewId={id}; UploadId={request.UploadId}; ScanId={scan.PiiScanId}; Acknowledged=True; Justification recorded in restricted PiiReviewRequests table.",
            HttpContext.Connection.RemoteIpAddress?.ToString());
        await _notifications.NotifyRequestedAsync(id, request.UploadId, email);
        TempData["Message"] = "PII review submitted. This upload is still blocked; no recipient access has been granted.";
        return RedirectToAction(nameof(Review), new { uploadId = request.UploadId });
    }
}
