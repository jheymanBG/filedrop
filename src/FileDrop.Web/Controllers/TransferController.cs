using Microsoft.AspNetCore.Http;
using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using System.Security.Claims;

namespace FileDrop.Web.Controllers;

public sealed class TransferController : Controller
{
    private readonly IConfiguration _config;
    private readonly IStorageService _storage;
    private readonly ITokenService _tokens;
    private readonly ITransferRepository _repo;
    private readonly IEmailService _email;
    private readonly IAuditRepository _audit;
    private readonly IVirusScanService _virusScan;
    private readonly IFileScanRepository _scanRepo;
    private readonly IPiiScanService _piiScan;
    private readonly IPiiScanRepository _piiScanRepo;
    private readonly ISettingsRepository _settings;
    private readonly IChunkedUploadService _chunkedUploads;
    private readonly IUploadPolicyService _uploadPolicy;
    private readonly IDownloadNotificationService _downloadNotifications;
    private readonly ITransferNotificationService _transferNotifications;
    private readonly ILogger<TransferController> _logger;

    public TransferController(
        IConfiguration config,
        IStorageService storage,
        ITokenService tokens,
        ITransferRepository repo,
        IEmailService email,
        IAuditRepository audit,
        IVirusScanService virusScan,
        IFileScanRepository scanRepo,
        IPiiScanService piiScan,
        IPiiScanRepository piiScanRepo,
        ISettingsRepository settings,
        IChunkedUploadService chunkedUploads,
        IUploadPolicyService uploadPolicy,
        IDownloadNotificationService downloadNotifications,
        ITransferNotificationService transferNotifications,
        ILogger<TransferController> logger)
    {
        _config = config;
        _storage = storage;
        _tokens = tokens;
        _repo = repo;
        _email = email;
        _audit = audit;
        _virusScan = virusScan;
        _scanRepo = scanRepo;
        _piiScan = piiScan;
        _piiScanRepo = piiScanRepo;
        _settings = settings;
        _chunkedUploads = chunkedUploads;
        _uploadPolicy = uploadPolicy;
        _downloadNotifications = downloadNotifications;
        _transferNotifications = transferNotifications;
        _logger = logger;
    }

    // Fail closed: pending, rejected, disabled, and unknown statuses never grant file access.
    private static bool IsDownloadAllowed(TransferRecord transfer) =>
        string.Equals(transfer.Status, "Active", StringComparison.OrdinalIgnoreCase);

    [Authorize]
    [HttpGet]
    public IActionResult Create()
    {
        return View();
    }

    [Authorize]
    [HttpPost]
    [RequestSizeLimit(long.MaxValue)]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        try
        {
            var form = await Request.ReadFormAsync(cancellationToken);
            var recipientEmail = form["RecipientEmail"].ToString().Trim();
            var manualSenderEmail = form["ManualSenderEmail"].ToString().Trim();
            var manualSenderName = form["ManualSenderName"].ToString().Trim();
            var subject = form["Subject"].ToString();
            var message = form["Message"].ToString();
            var expirationText = form["ExpirationDays"].ToString();
            var disableAfterFirstDownload = form["DisableAfterFirstDownload"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
            int? maxDownloads = int.TryParse(form["MaxDownloads"].ToString(), out var parsedMaxDownloads) ? parsedMaxDownloads : null;

            var uploadedIds = form["UploadedIds"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty)
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToList();

            if (!int.TryParse(expirationText, out var expirationDays))
            {
                expirationDays = _config.GetValue<int>("Transfers:DefaultExpirationDays", 7);
            }

            var files = form.Files.Where(f => f.Length > 0).ToList();
            var completedChunkFiles = await _chunkedUploads.GetCompletedAsync(uploadedIds);

            _logger.LogWarning("PHASE37 Transfer/Create POST received. User={User}; UploadedIdsRaw={UploadedIdsRaw}; UploadedIdsParsed={UploadedIds}; ChunkedFiles={ChunkedCount}; FormFiles={FormFileCount}; Recipient={Recipient}; Subject={Subject}; ContentLength={ContentLength}",
                User?.Identity?.Name, form["UploadedIds"].ToString(), string.Join(',', uploadedIds), completedChunkFiles.Count, files.Count, recipientEmail, subject, Request.ContentLength);

            ValidateTransferInputs(recipientEmail, expirationDays, maxDownloads, files.Count + completedChunkFiles.Count);

            var isAuthenticated = User?.Identity?.IsAuthenticated == true;
            var requireMicrosoftLogin = (_config["Security:RequireMicrosoftLoginForUploads"] ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);

            if (requireMicrosoftLogin && !isAuthenticated)
            {
                ModelState.AddModelError("", "Microsoft 365 sign-in is required to create transfers.");
            }

            if (!isAuthenticated && string.IsNullOrWhiteSpace(manualSenderEmail))
            {
                ModelState.AddModelError("ManualSenderEmail", "Sender email is required while Microsoft sign-in is pending.");
            }

            if (files.Count > 0)
            {
                var policy = await _uploadPolicy.ValidateAsync(files, subject);
                if (!policy.IsValid)
                {
                    foreach (var error in policy.Errors)
                    {
                        ModelState.AddModelError("Files", error);
                    }

                    _logger.LogWarning("Upload policy failed. User={User}; Errors={Errors}", User?.Identity?.Name, string.Join("; ", policy.Errors));
                }
            }

            if (!ModelState.IsValid)
            {
                return View();
            }

            var senderEmail = isAuthenticated ? GetAuthenticatedUserEmail() : manualSenderEmail;
            var senderName = isAuthenticated
                ? GetAuthenticatedUserName(senderEmail)
                : (string.IsNullOrWhiteSpace(manualSenderName) ? manualSenderEmail : manualSenderName);

            var result = await CreateTransferAsync(
                senderEmail,
                senderName,
                recipientEmail,
                subject,
                message,
                expirationDays,
                maxDownloads,
                disableAfterFirstDownload,
                completedChunkFiles,
                files,
                cancellationToken);

            ViewBag.DownloadLink = result.downloadLink;
            return View("Created", result.transfer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transfer creation failed.");
            ModelState.AddModelError("", ex.Message);
            return View();
        }
    }

    [Authorize]
    [HttpPost("/Transfer/CreateFromUploads")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> CreateFromUploads([FromBody] CreateTransferFromUploadsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request is null)
            {
                return BadRequest(new { error = "Transfer request was empty." });
            }

            request.UploadedIds = request.UploadedIds.Where(x => x != Guid.Empty).Distinct().ToList();
            var completedChunkFiles = await _chunkedUploads.GetCompletedAsync(request.UploadedIds);

            _logger.LogInformation("CreateFromUploads received. User={User}; UploadedIds={UploadedIds}; CompletedFiles={CompletedCount}; Recipient={Recipient}",
                User?.Identity?.Name, string.Join(',', request.UploadedIds), completedChunkFiles.Count, request.RecipientEmail);

            if (request.UploadedIds.Count == 0)
            {
                return BadRequest(new { error = "No completed upload IDs were posted to the transfer pipeline." });
            }

            if (completedChunkFiles.Count != request.UploadedIds.Count)
            {
                return BadRequest(new
                {
                    error = $"Only {completedChunkFiles.Count} of {request.UploadedIds.Count} uploaded file(s) are complete. Refresh the page and retry the upload."
                });
            }

            var validationErrors = ValidateTransferRequest(request, completedChunkFiles.Count);
            if (validationErrors.Count > 0)
            {
                return BadRequest(new { error = string.Join(" ", validationErrors), errors = validationErrors });
            }

            var senderEmail = GetAuthenticatedUserEmail();
            var senderName = GetAuthenticatedUserName(senderEmail);

            var result = await CreateTransferAsync(
                senderEmail,
                senderName,
                request.RecipientEmail.Trim(),
                request.Subject,
                request.Message,
                request.ExpirationDays,
                request.MaxDownloads,
                request.DisableAfterFirstDownload,
                completedChunkFiles,
                Array.Empty<IFormFile>(),
                cancellationToken);

            return Json(new CreateTransferFromUploadsResponse
            {
                Success = true,
                TransferId = result.transfer.TransferId,
                DownloadToken = result.transfer.DownloadToken,
                DownloadLink = result.downloadLink,
                RedirectUrl = Url.Action("Created", "Transfer", new { id = result.transfer.DownloadToken }) ?? $"/Transfer/Created/{result.transfer.DownloadToken}",
                FileCount = result.files.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateFromUploads failed.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpGet("/Transfer/Created/{id}")]
    public async Task<IActionResult> Created(string id)
    {
        var result = await _repo.GetByTokenAsync(id);
        if (result.transfer is null) return NotFound("Transfer not found.");

        if (!IsDownloadAllowed(result.transfer)) return NotFound();

        ViewBag.DownloadLink = Url.Action("Download", "Transfer", new { id = result.transfer.DownloadToken }, Request.Scheme)
            ?? $"/Transfer/Download/{result.transfer.DownloadToken}";

        return View("Created", result.transfer);
    }

    [AllowAnonymous]
    [HttpGet("/Transfer/Download/{id}")]
    public async Task<IActionResult> Download(string id)
    {
        var result = await _repo.GetByTokenAsync(id);
        if (result.transfer is null) return NotFound("Transfer not found.");
        if (!IsDownloadAllowed(result.transfer)) return NotFound();
        if (result.transfer.ExpirationDate < DateTime.UtcNow) return BadRequest("This link has expired.");

        return View(new DownloadViewModel { Transfer = result.transfer, Files = result.files });
    }

    [AllowAnonymous]
    [HttpGet("/Transfer/File/{token}/{fileId:guid}")]
    public async Task<IActionResult> File(string token, Guid fileId)
    {
        var result = await _repo.GetByTokenAsync(token);
        if (result.transfer is null || result.transfer.ExpirationDate < DateTime.UtcNow) return NotFound();
        if (!IsDownloadAllowed(result.transfer)) return NotFound();

        var file = result.files.FirstOrDefault(f => f.FileId == fileId);
        if (file is null || !System.IO.File.Exists(file.StoragePath)) return NotFound();

        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        await _audit.WriteAsync(result.transfer.TransferId, result.transfer.RecipientEmail,
            "Download Started", file.OriginalFileName, remoteIp);

        // Stream under the request cancellation token. Do not report completion until
        // copying and flushing the response have succeeded.
        return new FileCallbackResult(file.ContentType ?? "application/octet-stream", async (output, context) =>
        {
            try
            {
                await using (var input = new FileStream(file.StoragePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    context.HttpContext.Response.ContentLength = input.Length;
                    await input.CopyToAsync(output, 81920, context.HttpContext.RequestAborted);
                }
                await output.FlushAsync(context.HttpContext.RequestAborted);
                context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                await RecordDownloadInterruptedAsync(result.transfer.TransferId, result.transfer.RecipientEmail,
                    file.OriginalFileName, remoteIp, ex);
                throw;
            }

            await RecordDownloadCompletedAsync(result.transfer, result.files, file.OriginalFileName, remoteIp,
                context.HttpContext);
        })
        {
            FileDownloadName = file.OriginalFileName
        };
    }

    [AllowAnonymous]
    [HttpGet("/Transfer/DownloadAll/{token}")]
    public async Task<IActionResult> DownloadAll(string token)
    {
        var result = await _repo.GetByTokenAsync(token);
        if (result.transfer is null || result.transfer.ExpirationDate < DateTime.UtcNow) return NotFound();
        if (!IsDownloadAllowed(result.transfer)) return NotFound();

        // Do not silently deliver an incomplete archive if a stored file has gone missing.
        if (result.files.Count == 0 || result.files.Any(file => !System.IO.File.Exists(file.StoragePath)))
            return NotFound();

        var safeSubject = string.IsNullOrWhiteSpace(result.transfer.Subject)
            ? "FileDrop"
            : string.Concat(result.transfer.Subject.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));

        var zipName = $"{safeSubject}-{DateTime.Now:yyyyMMdd-HHmm}.zip";
        var detail = $"Download All ZIP: {zipName}";
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        await _audit.WriteAsync(result.transfer.TransferId, result.transfer.RecipientEmail,
            "Download Started", detail, remoteIp);

        // ZipArchive.Dispose writes the ZIP central directory synchronously.
        // Writing directly to Kestrel's response stream can throw InvalidOperationException
        // because synchronous response I/O is disabled. Finish the archive in a temporary
        // file before asynchronously streaming it to the recipient.
        string? tempZipPath = null;
        try
        {
            tempZipPath = Path.GetTempFileName();
            await using (var zipFile = new FileStream(tempZipPath, FileMode.Create,
                FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(zipFile, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in result.files)
                    {
                        HttpContext.RequestAborted.ThrowIfCancellationRequested();

                        var entryName = MakeUniqueName(usedEntryNames, file.OriginalFileName);
                        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                        await using var entryStream = entry.Open();
                        await using var input = new FileStream(file.StoragePath, FileMode.Open,
                            FileAccess.Read, FileShare.Read, 81920,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await input.CopyToAsync(entryStream, 81920, HttpContext.RequestAborted);
                    }
                }

                await zipFile.FlushAsync(HttpContext.RequestAborted);
            }

            HttpContext.RequestAborted.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            if (tempZipPath is not null)
            {
                try { System.IO.File.Delete(tempZipPath); }
                catch (Exception deleteError)
                {
                    _logger.LogWarning(deleteError, "Unable to remove failed temporary ZIP.");
                }
            }

            await RecordDownloadInterruptedAsync(result.transfer.TransferId, result.transfer.RecipientEmail,
                detail, remoteIp, ex);
            throw;
        }

        // Clean up even if ASP.NET Core never invokes the result callback.
        var completedZipPath = tempZipPath!;
        HttpContext.Response.OnCompleted(() =>
        {
            try { System.IO.File.Delete(completedZipPath); }
            catch (Exception deleteError)
            {
                _logger.LogWarning(deleteError, "Unable to remove completed temporary ZIP.");
            }
            return Task.CompletedTask;
        });

        return new FileCallbackResult("application/zip", async (output, context) =>
        {
            try
            {
                await using (var input = new FileStream(completedZipPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    context.HttpContext.Response.ContentLength = input.Length;
                    await input.CopyToAsync(output, 81920, context.HttpContext.RequestAborted);
                }

                await output.FlushAsync(context.HttpContext.RequestAborted);
                context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                await RecordDownloadInterruptedAsync(result.transfer.TransferId, result.transfer.RecipientEmail,
                    detail, remoteIp, ex);
                throw;
            }

            await RecordDownloadCompletedAsync(result.transfer, result.files, detail, remoteIp,
                context.HttpContext);
        })
        {
            FileDownloadName = zipName
        };
    }

    private async Task RecordDownloadInterruptedAsync(Guid transferId, string recipientEmail,
        string detail, string? remoteIp, Exception exception)
    {
        _logger.LogWarning(exception, "FileDrop download interrupted. TransferId={TransferId}; File={File}",
            transferId, detail);
        try
        {
            await _audit.WriteAsync(transferId, recipientEmail, "Download Interrupted",
                $"{detail}; Reason={exception.GetType().Name}", remoteIp);
        }
        catch (Exception auditError)
        {
            _logger.LogError(auditError, "Unable to audit interrupted FileDrop download. TransferId={TransferId}",
                transferId);
        }
    }

    private async Task RecordDownloadCompletedAsync(TransferRecord transfer,
        List<TransferFileRecord> files, string detail, string? remoteIp, HttpContext httpContext)
    {
        // A completed server-side copy is not proof the recipient saved or opened the file.
        // Completion notifications are best-effort and must not change a finished response.
        try
        {
            await _repo.MarkDownloadedAsync(transfer.TransferId);
            await _audit.WriteAsync(transfer.TransferId, transfer.RecipientEmail,
                "Download Completed", detail, remoteIp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record completed FileDrop download. TransferId={TransferId}",
                transfer.TransferId);
        }

        try
        {
            await _email.SendDownloadNotificationAsync(transfer, files, detail, remoteIp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send FileDrop download email. TransferId={TransferId}",
                transfer.TransferId);
        }

        try
        {
            await _downloadNotifications.NotifySenderAsync(transfer.TransferId, detail, httpContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send FileDrop sender notification. TransferId={TransferId}",
                transfer.TransferId);
        }
    }

    private async Task<(TransferRecord transfer, List<TransferFileRecord> files, string downloadLink)> CreateTransferAsync(
        string senderEmail,
        string? senderName,
        string recipientEmail,
        string? subject,
        string? message,
        int expirationDays,
        int? maxDownloads,
        bool disableAfterFirstDownload,
        IReadOnlyList<ChunkedUploadedFile> completedChunkFiles,
        IReadOnlyList<IFormFile> directFiles,
        CancellationToken cancellationToken)
    {
        var transfer = new TransferRecord
        {
            TransferId = Guid.NewGuid(),
            SenderEmail = senderEmail,
            SenderName = senderName,
            RecipientEmail = recipientEmail,
            Subject = subject,
            Message = message,
            DownloadToken = _tokens.CreateToken(),
            ExpirationDate = DateTime.UtcNow.AddDays(expirationDays),
            MaxDownloads = maxDownloads,
            DisableAfterFirstDownload = disableAfterFirstDownload
        };

        var savedFiles = new List<TransferFileRecord>();

        foreach (var completed in completedChunkFiles)
        {
            var fileId = Guid.NewGuid();
            var acceptedPath = completed.StoragePath;

            if (string.IsNullOrWhiteSpace(acceptedPath) || !System.IO.File.Exists(acceptedPath))
            {
                throw new InvalidOperationException($"{completed.OriginalFileName} completed upload session is missing from storage.");
            }

            await EnforcePiiPolicyAsync(completed.UploadId, completed.OriginalFileName, acceptedPath, cancellationToken);

            var scanBlocked = await ScanAndRecordAsync(
                transfer.TransferId,
                fileId,
                completed.OriginalFileName,
                acceptedPath,
                completed.Sha256Hash,
                cancellationToken);

            if (scanBlocked.blocked)
            {
                await _audit.WriteAsync(transfer.TransferId, senderEmail, "Upload Blocked By Security Scan", $"{completed.OriginalFileName}; Result={scanBlocked.result}", HttpContext.Connection.RemoteIpAddress?.ToString());
                throw new InvalidOperationException($"{completed.OriginalFileName} failed security scanning and was not accepted.");
            }

            savedFiles.Add(new TransferFileRecord
            {
                FileId = fileId,
                TransferId = transfer.TransferId,
                OriginalFileName = completed.OriginalFileName,
                StoredFileName = completed.StoredFileName,
                StoragePath = acceptedPath,
                ContentType = string.IsNullOrWhiteSpace(completed.ContentType) ? "application/octet-stream" : completed.ContentType,
                FileSizeBytes = completed.FileSizeBytes,
                Sha256Hash = completed.Sha256Hash
            });
        }

        foreach (var file in directFiles)
        {
            var saved = await _storage.SaveFileAsync(file, transfer.TransferId, cancellationToken);
            var fileId = Guid.NewGuid();
            var originalName = Path.GetFileName(file.FileName);

            await EnforceDirectFilePiiPolicyAsync(originalName, saved.storagePath, cancellationToken);

            var scanBlocked = await ScanAndRecordAsync(
                transfer.TransferId,
                fileId,
                originalName,
                saved.storagePath,
                saved.sha256,
                cancellationToken);

            if (scanBlocked.blocked)
            {
                await _audit.WriteAsync(transfer.TransferId, senderEmail, "Upload Blocked By Security Scan", $"{originalName}; Result={scanBlocked.result}", HttpContext.Connection.RemoteIpAddress?.ToString());
                throw new InvalidOperationException($"{originalName} failed security scanning and was not accepted.");
            }

            savedFiles.Add(new TransferFileRecord
            {
                FileId = fileId,
                TransferId = transfer.TransferId,
                OriginalFileName = originalName,
                StoredFileName = saved.storedFileName,
                StoragePath = saved.storagePath,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                FileSizeBytes = file.Length,
                Sha256Hash = saved.sha256
            });
        }

        if (savedFiles.Count == 0)
        {
            throw new InvalidOperationException("No completed files were available to create the transfer.");
        }

        _logger.LogWarning("PHASE37 inserting transfer. TransferId={TransferId}; Files={FileCount}; Recipient={Recipient}; Sender={Sender}", transfer.TransferId, savedFiles.Count, recipientEmail, senderEmail);

        await _repo.CreateTransferAsync(transfer, savedFiles);

        _logger.LogWarning("PHASE37 inserted transfer. TransferId={TransferId}; Files={FileCount}", transfer.TransferId, savedFiles.Count);

        var downloadLink = Url.Action("Download", "Transfer", new { id = transfer.DownloadToken }, Request.Scheme)
            ?? $"/Transfer/Download/{transfer.DownloadToken}";

        if (IsDownloadAllowed(transfer))
            await _transferNotifications.NotifyUploadCompleteAsync(transfer, savedFiles, downloadLink, HttpContext);

        await _audit.WriteAsync(
            transfer.TransferId,
            senderEmail,
            "Transfer Created",
            $"Recipient={recipientEmail}; Files={savedFiles.Count}; Subject={subject}",
            HttpContext.Connection.RemoteIpAddress?.ToString());

        _logger.LogInformation("Transfer created from upload pipeline. TransferId={TransferId}; Recipient={Recipient}; Sender={Sender}; Files={FileCount}",
            transfer.TransferId, recipientEmail, senderEmail, savedFiles.Count);

        return (transfer, savedFiles, downloadLink);
    }

    private async Task EnforcePiiPolicyAsync(Guid uploadId, string originalFileName, string storagePath, CancellationToken cancellationToken)
    {
        var enabled = await _settings.GetBoolAsync("Security.PiiScanning.Enabled", true);
        if (!enabled) return;

        var existing = await _piiScanRepo.GetLatestAsync(uploadId);
        if (existing is not null)
        {
            if (!existing.Allowed)
            {
                await _chunkedUploads.BlockAsync(uploadId, "PII policy blocked transfer creation.", cancellationToken);
                throw new InvalidOperationException($"{originalFileName} was blocked because the PII inspection did not pass.");
            }
            return;
        }

        var result = await _piiScan.ScanAsync(storagePath, originalFileName, cancellationToken);
        await _piiScanRepo.SaveAsync(uploadId, originalFileName, result);
        if (!result.Allowed)
        {
            await _chunkedUploads.BlockAsync(uploadId, "PII policy blocked transfer creation.", cancellationToken);
            await _audit.WriteAsync(null, User?.Identity?.Name, "PII Inspection Blocked Transfer", $"UploadId={uploadId}; File={originalFileName}; Result={result.Result}; Findings={result.FindingCount}; Categories={result.Categories}", HttpContext.Connection.RemoteIpAddress?.ToString());
            throw new InvalidOperationException($"{originalFileName} was blocked because potential PII was detected or the PII inspection failed.");
        }
    }

    private async Task EnforceDirectFilePiiPolicyAsync(string originalFileName, string storagePath, CancellationToken cancellationToken)
    {
        var enabled = await _settings.GetBoolAsync("Security.PiiScanning.Enabled", true);
        if (!enabled) return;

        var result = await _piiScan.ScanAsync(storagePath, originalFileName, cancellationToken);
        if (result.Allowed) return;

        try { System.IO.File.Delete(storagePath); } catch { }
        await _audit.WriteAsync(null, User?.Identity?.Name, "PII Inspection Blocked Direct Upload", $"File={originalFileName}; Result={result.Result}; Findings={result.FindingCount}; Categories={result.Categories}", HttpContext.Connection.RemoteIpAddress?.ToString());
        throw new InvalidOperationException($"{originalFileName} was blocked because potential PII was detected or the PII inspection failed.");
    }

    private void ValidateTransferInputs(string recipientEmail, int expirationDays, int? maxDownloads, int fileCount)
    {
        var maxDays = _config.GetValue<int>("Transfers:MaximumExpirationDays", 30);
        if (expirationDays < 1 || expirationDays > maxDays)
        {
            ModelState.AddModelError("ExpirationDays", $"Expiration must be between 1 and {maxDays} days.");
        }

        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            ModelState.AddModelError("RecipientEmail", "Recipient email is required.");
        }

        if (maxDownloads is < 1)
        {
            ModelState.AddModelError("MaxDownloads", "Maximum downloads must be greater than zero.");
        }

        if (fileCount == 0)
        {
            ModelState.AddModelError("Files", "At least one completed file is required.");
        }
    }

    private List<string> ValidateTransferRequest(CreateTransferFromUploadsRequest request, int completedFileCount)
    {
        var errors = new List<string>();
        var maxDays = _config.GetValue<int>("Transfers:MaximumExpirationDays", 30);

        if (string.IsNullOrWhiteSpace(request.RecipientEmail))
        {
            errors.Add("Recipient email is required.");
        }

        if (request.ExpirationDays < 1 || request.ExpirationDays > maxDays)
        {
            errors.Add($"Expiration must be between 1 and {maxDays} days.");
        }

        if (request.MaxDownloads is < 1)
        {
            errors.Add("Maximum downloads must be greater than zero.");
        }

        if (completedFileCount == 0)
        {
            errors.Add("At least one completed file is required.");
        }

        return errors;
    }

    private async Task<(bool blocked, string result)> ScanAndRecordAsync(Guid transferId, Guid fileId, string originalName, string storagePath, string? sha256, CancellationToken cancellationToken)
    {
        var enableScanning = await _settings.GetBoolAsync("Security.EnableVirusScanning", true);
        if (!enableScanning) return (false, "Skipped");

        var quarantineOnFailure = await _settings.GetBoolAsync("Security.QuarantineOnScanFailure", true);
        var blockUnscanned = await _settings.GetBoolAsync("Security.BlockUnscannedFiles", true);

        var scan = await _virusScan.ScanAsync(storagePath, cancellationToken);
        var scanStoragePath = storagePath;
        var unavailableBehavior = await _settings.GetStringAsync("Security.MalwareScanner.UnavailableBehavior", blockUnscanned ? "Block" : "AllowAndLog");
        var threatDetected = scan.IsThreat;

        // Quarantine actual malware. Scanner/provider failures remain scanner failures so they can
        // follow the configured unavailable behavior instead of being mislabeled as malware.
        if (threatDetected && quarantineOnFailure)
        {
            var quarantinePath = await _virusScan.QuarantineAsync(storagePath, originalName, transferId, cancellationToken);
            scanStoragePath = quarantinePath ?? storagePath;
            scan.Result = "Quarantined";
        }

        await _scanRepo.AddAsync(new FileScanRecord
        {
            TransferId = transferId,
            FileId = fileId,
            OriginalFileName = originalName,
            StoragePath = scanStoragePath,
            Sha256Hash = sha256,
            Engine = scan.Engine,
            Result = scan.Result,
            ThreatName = scan.ThreatName,
            Details = scan.Details
        });

        var isThreat = scan.IsThreat;
        var blocked = isThreat || (scan.IsUnavailable && unavailableBehavior.Equals("Block", StringComparison.OrdinalIgnoreCase));
        return (blocked, scan.Result);
    }

    private string GetAuthenticatedUserEmail()
    {
        var principal = HttpContext.User;
        return principal.FindFirstValue("preferred_username")
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.Identity?.Name
            ?? "unknown@bgohio.gov";
    }

    private string GetAuthenticatedUserName(string fallbackEmail)
    {
        var principal = HttpContext.User;
        return principal.FindFirstValue("name")
            ?? principal.Identity?.Name
            ?? fallbackEmail;
    }

    private static string MakeUniqueName(HashSet<string> usedEntryNames, string fileName)
    {
        // ZipArchive.GetEntry is not supported when the archive is in Create mode.
        // Track assigned names instead of querying the incomplete archive.
        var clean = string.IsNullOrWhiteSpace(fileName) ? "file.bin" : Path.GetFileName(fileName);
        if (usedEntryNames.Add(clean)) return clean;

        var name = Path.GetFileNameWithoutExtension(clean);
        var ext = Path.GetExtension(clean);
        for (var i = 2; i < 9999; i++)
        {
            var candidate = $"{name} ({i}){ext}";
            if (usedEntryNames.Add(candidate)) return candidate;
        }

        string fallback;
        do { fallback = $"{Guid.NewGuid():N}-{clean}"; }
        while (!usedEntryNames.Add(fallback));
        return fallback;
    }
}

public sealed class FileCallbackResult : FileResult
{
    private readonly Func<Stream, ActionContext, Task> _callback;

    public FileCallbackResult(string contentType, Func<Stream, ActionContext, Task> callback) : base(contentType)
    {
        _callback = callback;
    }

    public override async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.ContentType = ContentType.ToString();

        if (!string.IsNullOrWhiteSpace(FileDownloadName))
        {
            var encodedName = Uri.EscapeDataString(FileDownloadName);
            response.Headers.ContentDisposition = $"attachment; filename=\"{FileDownloadName}\"; filename*=UTF-8''{encodedName}";
        }

        await _callback(response.Body, context);
    }
}
