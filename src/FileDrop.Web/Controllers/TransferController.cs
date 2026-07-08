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
        _chunkedUploads = chunkedUploads;
        _uploadPolicy = uploadPolicy;
        _downloadNotifications = downloadNotifications;
        _transferNotifications = transferNotifications;
        _logger = logger;
    }

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
        if (result.transfer.Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) return BadRequest("This link has been disabled.");
        if (result.transfer.ExpirationDate < DateTime.UtcNow) return BadRequest("This link has expired.");

        return View(new DownloadViewModel { Transfer = result.transfer, Files = result.files });
    }

    [AllowAnonymous]
    [HttpGet("/Transfer/File/{token}/{fileId:guid}")]
    public async Task<IActionResult> File(string token, Guid fileId)
    {
        var result = await _repo.GetByTokenAsync(token);
        if (result.transfer is null || result.transfer.ExpirationDate < DateTime.UtcNow) return NotFound();
        if (result.transfer.Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) return BadRequest("This link has been disabled.");

        var file = result.files.FirstOrDefault(f => f.FileId == fileId);
        if (file is null || !System.IO.File.Exists(file.StoragePath)) return NotFound();

        await _repo.MarkDownloadedAsync(result.transfer.TransferId);
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        await _audit.WriteAsync(result.transfer.TransferId, result.transfer.RecipientEmail, "File Downloaded", file.OriginalFileName, remoteIp);
        await _email.SendDownloadNotificationAsync(result.transfer, result.files, file.OriginalFileName, remoteIp);
        await _downloadNotifications.NotifySenderAsync(result.transfer.TransferId, file.OriginalFileName, HttpContext);

        return PhysicalFile(file.StoragePath, file.ContentType ?? "application/octet-stream", file.OriginalFileName);
    }

    [AllowAnonymous]
    [HttpGet("/Transfer/DownloadAll/{token}")]
    public async Task<IActionResult> DownloadAll(string token)
    {
        var result = await _repo.GetByTokenAsync(token);
        if (result.transfer is null || result.transfer.ExpirationDate < DateTime.UtcNow) return NotFound();
        if (result.transfer.Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) return BadRequest("This link has been disabled.");

        var safeSubject = string.IsNullOrWhiteSpace(result.transfer.Subject)
            ? "FileDrop"
            : string.Concat(result.transfer.Subject.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));

        var zipName = $"{safeSubject}-{DateTime.Now:yyyyMMdd-HHmm}.zip";
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        await _audit.WriteAsync(result.transfer.TransferId, result.transfer.RecipientEmail, "Download All ZIP", $"Files={result.files.Count}; Zip={zipName}", remoteIp);
        await _repo.MarkDownloadedAsync(result.transfer.TransferId);
        await _email.SendDownloadNotificationAsync(result.transfer, result.files, $"Download All ZIP: {zipName}", remoteIp);
        await _downloadNotifications.NotifySenderAsync(result.transfer.TransferId, $"Download All ZIP: {zipName}", HttpContext);

        return new FileCallbackResult("application/zip", async (output, _) =>
        {
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in result.files)
            {
                if (!System.IO.File.Exists(file.StoragePath)) continue;
                var entryName = MakeUniqueName(archive, file.OriginalFileName);
                var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await using var input = System.IO.File.OpenRead(file.StoragePath);
                await input.CopyToAsync(entryStream);
            }
        })
        {
            FileDownloadName = zipName
        };
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
        var enableScanning = (_config["Security:EnableVirusScanning"] ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!enableScanning) return (false, "Skipped");

        var quarantineOnFailure = (_config["Security:QuarantineOnScanFailure"] ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase);
        var blockUnscanned = (_config["Security:BlockUnscannedFiles"] ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase);

        var scan = await _virusScan.ScanAsync(storagePath, cancellationToken);
        var scanStoragePath = storagePath;

        if (!scan.IsClean && quarantineOnFailure)
        {
            var quarantinePath = await _virusScan.QuarantineAsync(storagePath, originalName, transferId, cancellationToken);
            scanStoragePath = quarantinePath ?? storagePath;
            scan.Result = scan.Result.Equals("Infected", StringComparison.OrdinalIgnoreCase) ? "Infected" : "Quarantined";
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

        var blocked = !scan.IsClean && (blockUnscanned || scan.Result.Equals("Infected", StringComparison.OrdinalIgnoreCase) || scan.Result.Equals("Quarantined", StringComparison.OrdinalIgnoreCase));
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

    private static string MakeUniqueName(ZipArchive archive, string fileName)
    {
        var clean = string.IsNullOrWhiteSpace(fileName) ? "file.bin" : Path.GetFileName(fileName);
        if (archive.GetEntry(clean) is null) return clean;

        var name = Path.GetFileNameWithoutExtension(clean);
        var ext = Path.GetExtension(clean);
        for (var i = 2; i < 9999; i++)
        {
            var candidate = $"{name} ({i}){ext}";
            if (archive.GetEntry(candidate) is null) return candidate;
        }

        return $"{Guid.NewGuid():N}-{clean}";
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
