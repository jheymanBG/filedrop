using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Dapper;
using FileDrop.Web.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class PiiReviewAdminController : Controller
{
    private readonly PiiReviewRepository _reviews;
    private readonly IAuditRepository _audit;
    private readonly PiiReviewNotificationService _notifications;
    private readonly PiiReleaseService _release;
    private readonly IVirusScanService _malwareScanner;
    private readonly IConfiguration _config;
    private readonly ITransferNotificationService _transferNotifications;
    public PiiReviewAdminController(PiiReviewRepository reviews, IAuditRepository audit, PiiReviewNotificationService notifications, PiiReleaseService release, ITransferNotificationService transferNotifications, IConfiguration config, IVirusScanService malwareScanner)
    { _reviews = reviews; _audit = audit; _notifications = notifications; _release = release; _transferNotifications = transferNotifications; _config = config; _malwareScanner = malwareScanner; }

    [HttpGet("/Admin/Pii/Reviews")]
    public async Task<IActionResult> Index()
    {
        var pending = await _reviews.ListPendingAsync();
        var findings = new Dictionary<long, IReadOnlyList<FileDrop.Web.Models.PiiScanFindingRecord>>();
        foreach (var item in pending)
            findings[item.ReviewId] = await _reviews.GetFindingsAsync(item.PiiScanId);
        ViewBag.FindingMap = findings;
        return View(pending);
    }

    [HttpPost("/Admin/Pii/Reviews/ApproveRelease")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveRelease(long reviewId, string reason,
        int expirationDays = 7, int? maxDownloads = null, bool disableAfterFirstDownload = false,
        CancellationToken cancellationToken = default)
    {
        var reviewer = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("preferred_username") ?? User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(reviewer)) return Forbid();
        try
        {
            // Do not release a file if the emailed link would point at localhost.
            var baseUrl = _config["PiiReviewNotifications:BaseUrl"];
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
                || baseUri.Scheme != Uri.UriSchemeHttps
                || baseUri.IsLoopback)
                throw new InvalidOperationException("Configure PiiReviewNotifications:BaseUrl as the externally accessible TestDrop HTTPS URL before releasing files.");
            var pendingReview = await _reviews.GetByIdAsync(reviewId);
            if (string.IsNullOrWhiteSpace(pendingReview?.RecipientEmail))
                throw new InvalidOperationException("Original recipient email is missing; approval is blocked. Reject or delete the review instead.");
            var recipientEmail = pendingReview.RecipientEmail;
            var result = await _release.ApproveAsync(reviewId, reviewer, reason, recipientEmail,
                expirationDays, maxDownloads, disableAfterFirstDownload, cancellationToken);
            var link = new Uri(baseUri, "/Transfer/Download/" + Uri.EscapeDataString(result.transfer.DownloadToken)).ToString();
            try
            {
                await _audit.WriteAsync(result.transfer.TransferId, reviewer, "PII Approved and Released",
                    $"ReviewId={reviewId}; Recipient={recipientEmail}; Justification={reason}", HttpContext.Connection.RemoteIpAddress?.ToString());
                await _transferNotifications.NotifyUploadCompleteAsync(result.transfer, result.files, link, HttpContext);
                await _notifications.NotifyDecidedAsync(reviewId, "Approved");
            }
            catch (Exception error)
            {
                // Decision is already committed. Never retry a committed release due to SMTP failure.
                TempData["Message"] = $"File released, but post-release notification or audit failed: {error.GetType().Name}. Check server logs.";
                return RedirectToAction(nameof(Index));
            }
            TempData["Message"] = "PII exception approved and secure transfer created. Check delivery logs for email status.";
            return RedirectToAction(nameof(Index));
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [HttpPost("/Admin/Pii/Reviews/Decide")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(long reviewId, string decision, string reason)
    {
        var reviewer = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("preferred_username") ?? User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(reviewer) || decision != "Rejected"
            || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 15)
            return BadRequest("Valid reviewer, decision and 15-character reason required.");
        var updated = await _reviews.DecideAsync(reviewId, reviewer, false, reason);
        if (!updated) return Conflict("Review already decided or reviewer is the requester.");
        await _audit.WriteAsync(null, reviewer, "PII Review Decision",
            $"ReviewId={reviewId}; Decision={decision}; File remains blocked.",
            HttpContext.Connection.RemoteIpAddress?.ToString());
        await _notifications.NotifyDecidedAsync(reviewId, decision);
        TempData["Message"] = "Review rejected. File remains blocked.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/Pii/Reviews/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(long reviewId, string reason)
    {
        var administrator = User.FindFirstValue(ClaimTypes.Email)
            ?? User.FindFirstValue("preferred_username") ?? User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(administrator) || string.IsNullOrWhiteSpace(reason)
            || reason.Trim().Length < 15 || reason.Length > 2000)
            return BadRequest("Administrator and 15-2000 character deletion reason required.");

        // Record an audit attempt before the status update. If the audit backend
        // is unavailable, do not change the review state.
        await _audit.WriteAsync(null, administrator, "PII Review Delete Requested",
            $"ReviewId={reviewId}; Reason={reason.Trim()}; Held file must remain blocked.",
            HttpContext.Connection.RemoteIpAddress?.ToString());
        var updated = await _reviews.DeletePendingAsync(reviewId, administrator, reason);
        if (!updated) return Conflict("Review already decided or deleted.");
        await _audit.WriteAsync(null, administrator, "PII Review Deleted",
            $"ReviewId={reviewId}; Soft-deleted from pending queue; held file retained.",
            HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = $"Review #{reviewId} removed from pending queue. Held file remains blocked.";
        return RedirectToAction(nameof(Index));
    }

    // Administrator-only review download.
    // Does not approve or release the file to the recipient.
    [HttpGet("/Admin/Pii/Reviews/{reviewId:long}/File")]
    public async Task<IActionResult> ViewHeldFile(
        long reviewId,
        CancellationToken cancellationToken)
    {
        var admin = User.FindFirstValue(ClaimTypes.Email)
            ?? User.FindFirstValue("preferred_username")
            ?? User.Identity?.Name;

        if (reviewId < 1 || string.IsNullOrWhiteSpace(admin))
            return Forbid();

        var connectionString =
            _config.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
            return StatusCode(503);

        await using var db = new SqlConnection(connectionString);

        var file = await db.QuerySingleOrDefaultAsync<HeldFileForReview>(
            """
            SELECT
                u.FinalStoragePath AS StoragePath,
                u.OriginalFileName,
                u.TotalBytes,
                u.Sha256Hash,
                u.Status AS UploadStatus,
                r.Status AS ReviewStatus
            FROM dbo.PiiReviewRequests r
            JOIN dbo.ChunkedUploadSessions u
                ON u.UploadId = r.UploadId
            WHERE r.ReviewId = @reviewId
            """,
            new { reviewId });

        if (file is null
            || file.ReviewStatus != "Pending"
            || file.UploadStatus != "PendingPIIReview"
            || string.IsNullOrWhiteSpace(file.StoragePath)
            || string.IsNullOrWhiteSpace(file.Sha256Hash))
        {
            return NotFound();
        }

        if (!System.IO.File.Exists(file.StoragePath))
            return NotFound();

        var physical = new FileInfo(file.StoragePath);

        if (physical.Length != file.TotalBytes)
            return Conflict("File integrity verification failed.");

        await using (var stream =
            System.IO.File.OpenRead(file.StoragePath))
        {
            var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(
                    stream,
                    cancellationToken));

            if (!hash.Equals(
                file.Sha256Hash,
                StringComparison.OrdinalIgnoreCase))
            {
                return Conflict("File integrity verification failed.");
            }
        }

        // Fail closed if malware scanning cannot confirm a clean file.
        var scan = await _malwareScanner.ScanAsync(
            file.StoragePath,
            cancellationToken);

        // Administrator review only:
        // SentinelOne "Protected" verifies endpoint protection,
        // not an individual clean-file scan.
        // Recipient release requirements remain unchanged.
        var sentinelProtected =
            string.Equals(
                scan.Result,
                "Protected",
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                scan.Engine,
                "SentinelOne Endpoint Protection",
                StringComparison.OrdinalIgnoreCase);

        if (!scan.IsClean && !sentinelProtected)
        {
            return Conflict(
                "Administrator download blocked. Malware result: "
                + scan.Result);
        }

        // Audit the administrator's access before returning file bytes.
        await _audit.WriteAsync(
            null,
            admin,
            "PII Held File Access",
            $"ReviewId={reviewId}; administrator downloaded held file for review; no release granted.",
            HttpContext.Connection.RemoteIpAddress?.ToString());

        Response.Headers["Cache-Control"] =
            "no-store, no-cache, private";

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "sandbox";

        var safeName = Path.GetFileName(file.OriginalFileName);

        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "review-document.bin";

        return PhysicalFile(
            file.StoragePath,
            "application/octet-stream",
            safeName,
            enableRangeProcessing: false);
    }

    private sealed class HeldFileForReview
    {
        public string? StoragePath { get; set; }
        public string OriginalFileName { get; set; } = "";
        public long TotalBytes { get; set; }
        public string? Sha256Hash { get; set; }
        public string UploadStatus { get; set; } = "";
        public string ReviewStatus { get; set; } = "";
    }

}
