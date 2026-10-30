using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[Authorize]
public sealed class UploadProtectionController : Controller
{
    private readonly IStorageProtectionService _storageProtection;
    private readonly IChunkedUploadService _chunks;
    private readonly IVirusScanService _virusScan;
    private readonly IPiiScanService _piiScan;
    private readonly IPiiScanRepository _piiRepository;
    private readonly ISettingsRepository _settings;
    private readonly IAuditRepository _audit;
    private readonly ILogger<UploadProtectionController> _logger;

    public UploadProtectionController(
        IStorageProtectionService storageProtection,
        IChunkedUploadService chunks,
        IVirusScanService virusScan,
        IPiiScanService piiScan,
        IPiiScanRepository piiRepository,
        ISettingsRepository settings,
        IAuditRepository audit,
        ILogger<UploadProtectionController> logger)
    {
        _storageProtection = storageProtection;
        _chunks = chunks;
        _virusScan = virusScan;
        _piiScan = piiScan;
        _piiRepository = piiRepository;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    [HttpPost("/Upload/Preflight")]
    public async Task<IActionResult> Preflight([FromBody] UploadPreflightRequest request, CancellationToken cancellationToken)
    {
        var result = await _storageProtection.CheckAsync(request, cancellationToken);
        if (!result.Allowed)
            await _audit.WriteAsync(null, User?.Identity?.Name, "Upload Preflight Blocked", result.Message, HttpContext.Connection.RemoteIpAddress?.ToString());
        return Json(result);
    }

    [HttpPost("/Upload/ScanCompleted")]
    public async Task<IActionResult> ScanCompleted([FromBody] ScanCompletedUploadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var status = await _chunks.GetStatusAsync(request.UploadId);
            if (status is null || !status.Status.Equals("Complete", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new ScanCompletedUploadResponse { UploadId = request.UploadId, Allowed = false, Status = "Failed", Message = "Upload is not complete and cannot be inspected." });

            if (string.IsNullOrWhiteSpace(status.StoragePath) || !System.IO.File.Exists(status.StoragePath))
                return BadRequest(new ScanCompletedUploadResponse { UploadId = request.UploadId, Allowed = false, Status = "Failed", Message = "Completed upload file is missing from storage." });

            var preSubmitEnabled = await GetBoolAsync("Security.PreSubmitScanning.Enabled", true);
            var virusEnabled = await GetBoolAsync("Security.EnableVirusScanning", false);
            var unavailableBehavior = await _settings.GetStringAsync("Security.MalwareScanner.UnavailableBehavior", "Block");
            var piiEnabled = await GetBoolAsync("Security.PiiScanning.Enabled", true);

            var malwareStatus = "Disabled";
            string? threatName = null;
            string? malwareDetails = null;
            var malwareAllowed = true;

            if (preSubmitEnabled && virusEnabled)
            {
                var malware = await _virusScan.ScanAsync(status.StoragePath, cancellationToken);
                malwareStatus = malware.Result;
                threatName = malware.ThreatName;
                malwareDetails = malware.Details;
                malwareAllowed = malware.IsAcceptedProtection ||
                    (malware.IsUnavailable && !unavailableBehavior.Equals("Block", StringComparison.OrdinalIgnoreCase));
            }

            PiiScanResult? pii = null;
            if (preSubmitEnabled && piiEnabled)
            {
                pii = await _piiScan.ScanAsync(status.StoragePath, status.OriginalFileName ?? Path.GetFileName(status.StoragePath), cancellationToken);
                await _piiRepository.SaveAsync(request.UploadId, status.OriginalFileName ?? Path.GetFileName(status.StoragePath), pii);
                // Persist masked finding metadata for GIS review; never persist raw matches.
                // Requires Phase73B1 test-only schema. Do not deploy before migration.
                if (pii.Findings.Count > 0)
                {
                    var scanRecord = await _piiRepository.GetLatestAsync(request.UploadId);
                    if (scanRecord is not null)
                    {
                        await using var piiDb = new Microsoft.Data.SqlClient.SqlConnection(
                            HttpContext.RequestServices.GetRequiredService<IConfiguration>()
                                .GetConnectionString("DefaultConnection"));
                        foreach (var finding in pii.Findings)
                        {
                            // Avoid saving potentially unredacted context/locations.
                                                        // Phase73C: safe persisted finding with PDF page.
                            int? findingPage = null;

                            var loc = finding.Location ?? "";

                            if (loc.StartsWith("Page ", StringComparison.Ordinal) &&
                                int.TryParse(loc[5..], out var parsedPage) &&
                                parsedPage > 0)
                            {
                                findingPage = parsedPage;
                            }

                            // Never persist raw extracted text or context.
                            var candidate = finding.RedactedValue ?? "";

                            var masked =
                                candidate.Contains('*') &&
                                candidate.Length <= 80 &&
                                !candidate.Contains('\n') &&
                                !candidate.Contains('\r')
                                    ? candidate
                                    : "[redacted]";

                            await Dapper.SqlMapper.ExecuteAsync(piiDb, """
                                INSERT INTO dbo.PiiScanFindings
                                (PiiScanId, Category, Confidence, PageNumber, Location, MaskedEvidence, DetectionReason)
                                VALUES (@scanId, @category, @confidence, @findingPage, NULL, @masked, @reason)
                                """, new
                                {
                                    scanId = scanRecord.PiiScanId,
                                    findingPage,
                                    category = (finding.Category ?? "Potential PII")[..Math.Min((finding.Category ?? "Potential PII").Length, 100)],
                                    confidence = (finding.Confidence ?? "Unknown")[..Math.Min((finding.Confidence ?? "Unknown").Length, 30)],
                                    masked, reason = (finding.Reason ?? "Rule match")[..Math.Min((finding.Reason ?? "Rule match").Length, 2000)] });
                        }
                    }
                }
            }

            var requiresAck = malwareAllowed && (pii?.RequiresAcknowledgement ?? false);
            var piiHardBlock = pii is not null && !pii.Allowed && !pii.RequiresAcknowledgement;
            var allowed = malwareAllowed && (pii?.Allowed ?? true);
            var hardBlock = !malwareAllowed || piiHardBlock;

            if (hardBlock)
            {
                var reason = !malwareAllowed
                    ? malwareStatus.Equals("Infected", StringComparison.OrdinalIgnoreCase) || malwareStatus.Equals("Quarantined", StringComparison.OrdinalIgnoreCase)
                        ? $"Malware was detected and the upload was blocked ({malwareStatus})."
                        : $"Malware protection could not be verified and policy is configured to block ({malwareStatus})."
                    : $"PII scan blocked upload ({pii?.Result ?? "Unknown"}; confidence={pii?.MaxConfidence ?? "Unknown"}).";
                if (piiHardBlock && malwareAllowed &&
                    malwareStatus is not ("Infected" or "Quarantined"))
                    await _chunks.HoldForPiiReviewAsync(request.UploadId, cancellationToken);
                else
                    await _chunks.BlockAsync(request.UploadId, reason, cancellationToken);
            }

            var auditAction = hardBlock
                ? "Upload Pre-Submit Inspection Blocked"
                : requiresAck
                    ? "Upload PII Warning Awaiting Acknowledgement"
                    : "Upload Pre-Submit Inspection Passed";
            var piiSummary = pii is null
                ? "PII=Disabled"
                : $"PII={pii.Result}; Findings={pii.FindingCount}; Categories={pii.Categories}; MaxConfidence={pii.MaxConfidence}; RequiresAck={pii.RequiresAcknowledgement}";
            await _audit.WriteAsync(null, User?.Identity?.Name, auditAction,
                $"UploadId={request.UploadId}; File={status.OriginalFileName}; Malware={malwareStatus}; {piiSummary}",
                HttpContext.Connection.RemoteIpAddress?.ToString());

            var messageParts = new List<string>();
            if (preSubmitEnabled && virusEnabled)
            {
                if (malwareStatus.Equals("Protected", StringComparison.OrdinalIgnoreCase))
                    messageParts.Add("SentinelOne endpoint protection is active. No FileDrop on-demand malware scan was required.");
                else if (malwareStatus.Equals("Clean", StringComparison.OrdinalIgnoreCase))
                    messageParts.Add("Malware scan passed.");
                else if (malwareStatus.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                    messageParts.Add("Malware scanning is disabled by administrator policy.");
                else if (malwareStatus.Equals("Infected", StringComparison.OrdinalIgnoreCase) || malwareStatus.Equals("Quarantined", StringComparison.OrdinalIgnoreCase))
                    messageParts.Add("Malware was detected and this file is blocked.");
                else if (malwareAllowed)
                    messageParts.Add("The malware scanner was unavailable; administrator policy allows the upload and the condition was logged.");
                else
                    messageParts.Add("Malware protection could not be verified and administrator policy blocks this file.");
            }
            if (preSubmitEnabled && piiEnabled && pii is not null)
                messageParts.Add(pii.Message);
            if (!preSubmitEnabled)
                messageParts.Add("Pre-submit inspection is disabled by administrator policy.");
            else if (!virusEnabled && !piiEnabled)
                messageParts.Add("File inspection is disabled by administrator policy.");

            return Json(new ScanCompletedUploadResponse
            {
                UploadId = request.UploadId,
                Allowed = allowed,
                RequiresAcknowledgement = requiresAck,
                Status = hardBlock ? (!malwareAllowed ? malwareStatus : pii?.Result ?? "Blocked") : requiresAck ? "PII Warning" : "Clean",
                Message = string.Join(" ", messageParts),
                ThreatName = threatName,
                Details = malwareDetails,
                MalwareStatus = malwareStatus,
                PiiStatus = pii?.Result ?? "Disabled",
                PiiDetected = (pii?.FindingCount ?? 0) > 0,
                PiiFindingCount = pii?.FindingCount ?? 0,
                PiiMaxConfidence = pii?.MaxConfidence ?? "None",
                PiiCategories = pii?.Findings.Select(x => x.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>(),
                PiiFindings = pii?.Findings ?? new List<PiiFindingSummary>()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pre-submit inspection failed for UploadId={UploadId}", request.UploadId);
            try { await _chunks.BlockAsync(request.UploadId, "Pre-submit inspection failed.", cancellationToken); } catch { }
            return BadRequest(new ScanCompletedUploadResponse { UploadId = request.UploadId, Allowed = false, Status = "Failed", Message = "File inspection failed and the upload was not accepted." });
        }
    }

    [HttpPost("/Upload/AcknowledgePii")]
    public async Task<IActionResult> AcknowledgePii([FromBody] PiiAcknowledgementRequest request, CancellationToken cancellationToken)
    {
        if (!request.Accepted)
            return BadRequest(new { allowed = false, message = "The PII warning must be acknowledged before continuing." });

        var status = await _chunks.GetStatusAsync(request.UploadId);
        if (status is null || !status.Status.Equals("Complete", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { allowed = false, message = "The upload is no longer available for acknowledgement." });

        var latest = await _piiRepository.GetLatestAsync(request.UploadId);
        if (latest is null || !latest.Result.Equals("Warning", StringComparison.OrdinalIgnoreCase) || latest.Allowed)
            return BadRequest(new { allowed = false, message = "This upload does not have an acknowledgement-eligible PII warning." });

        var userName = User?.Identity?.Name;
        await _piiRepository.SaveAcknowledgementAsync(request.UploadId, status.OriginalFileName ?? Path.GetFileName(status.StoragePath ?? "upload"), latest, userName);
        await _audit.WriteAsync(null, userName, "PII Warning Accepted",
            $"UploadId={request.UploadId}; File={status.OriginalFileName}; Categories={latest.Categories}; Findings={latest.FindingCount}; Action=User acknowledged low/medium confidence PII warning and continued. No unredacted PII values logged.",
            HttpContext.Connection.RemoteIpAddress?.ToString());

        return Json(new
        {
            allowed = true,
            status = "WarningAccepted",
            message = "PII warning acknowledged. The upload may continue. The acknowledgement was recorded in the audit log."
        });
    }

    private async Task<bool> GetBoolAsync(string key, bool defaultValue)
    {
        var value = await _settings.GetValueAsync(key);
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }
}
