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
    private readonly IUploadPolicyService _uploadPolicy;
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
        IUploadPolicyService uploadPolicy,
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
        _uploadPolicy = uploadPolicy;
        _logger = logger;
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Create()
    {
        return View();
    }

    [AllowAnonymous]
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

            if (!int.TryParse(expirationText, out var expirationDays))
            {
                expirationDays = _config.GetValue<int>("Transfers:DefaultExpirationDays", 7);
            }

            var maxDays = _config.GetValue<int>("Transfers:MaximumExpirationDays", 30);

            if (expirationDays < 1 || expirationDays > maxDays)
            {
                ModelState.AddModelError("ExpirationDays", $"Expiration must be between 1 and {maxDays} days.");
            }

            if (string.IsNullOrWhiteSpace(recipientEmail))
            {
                ModelState.AddModelError("RecipientEmail", "Recipient email is required.");
            }

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

            var files = form.Files.Where(f => f.Length > 0).ToList();

            _logger.LogInformation("Upload POST received. User={User}; FormFiles={Count}; ContentLength={ContentLength}",
                User?.Identity?.Name, files.Count, Request.ContentLength);

            if (files.Count == 0)
            {
                ModelState.AddModelError("Files", "At least one file is required.");
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

            var senderEmail =
                User?.Identity?.IsAuthenticated == true
                    ? (User.FindFirstValue("preferred_username") ??
                       User.FindFirstValue(ClaimTypes.Email) ??
                       User.Identity?.Name ??
                       "unknown@bgohio.gov")
                    : manualSenderEmail;

            var senderName =
                User?.Identity?.IsAuthenticated == true
                    ? (User.FindFirstValue("name") ??
                       User.Identity?.Name ??
                       senderEmail)
                    : (string.IsNullOrWhiteSpace(manualSenderName) ? manualSenderEmail : manualSenderName);

            var transfer = new TransferRecord
            {
                TransferId = Guid.NewGuid(),
                SenderEmail = senderEmail,
                SenderName = senderName,
                RecipientEmail = recipientEmail,
                Subject = subject,
                Message = message,
                DownloadToken = _tokens.CreateToken(),
                ExpirationDate = DateTime.UtcNow.AddDays(expirationDays)
            };

            var savedFiles = new List<TransferFileRecord>();

            foreach (var file in files)
            {
                var saved = await _storage.SaveFileAsync(file, transfer.TransferId, cancellationToken);

                var fileId = Guid.NewGuid();
                var originalName = Path.GetFileName(file.FileName);

                var enableScanning = (_config["Security:EnableVirusScanning"] ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase);
                var quarantineOnFailure = (_config["Security:QuarantineOnScanFailure"] ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase);
                var blockUnscanned = (_config["Security:BlockUnscannedFiles"] ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase);

                if (enableScanning)
                {
                    var scan = await _virusScan.ScanAsync(saved.storagePath, cancellationToken);
                    var scanStoragePath = saved.storagePath;

                    if (!scan.IsClean)
                    {
                        if (quarantineOnFailure)
                        {
                            var quarantinePath = await _virusScan.QuarantineAsync(saved.storagePath, originalName, transfer.TransferId, cancellationToken);
                            scanStoragePath = quarantinePath ?? saved.storagePath;
                            scan.Result = scan.Result == "Infected" ? "Infected" : "Quarantined";
                        }

                        await _scanRepo.AddAsync(new FileScanRecord
                        {
                            TransferId = transfer.TransferId,
                            FileId = fileId,
                            OriginalFileName = originalName,
                            StoragePath = scanStoragePath,
                            Sha256Hash = saved.sha256,
                            Engine = scan.Engine,
                            Result = scan.Result,
                            ThreatName = scan.ThreatName,
                            Details = scan.Details
                        });

                        if (blockUnscanned || scan.Result.Equals("Infected", StringComparison.OrdinalIgnoreCase) || scan.Result.Equals("Quarantined", StringComparison.OrdinalIgnoreCase))
                        {
                            ModelState.AddModelError("Files", $"{originalName} failed security scanning and was not accepted.");
                            await _audit.WriteAsync(transfer.TransferId, senderEmail, "Upload Blocked By Security Scan", $"{originalName}; Result={scan.Result}", HttpContext.Connection.RemoteIpAddress?.ToString());
                            return View();
                        }
                    }

                    await _scanRepo.AddAsync(new FileScanRecord
                    {
                        TransferId = transfer.TransferId,
                        FileId = fileId,
                        OriginalFileName = originalName,
                        StoragePath = scanStoragePath,
                        Sha256Hash = saved.sha256,
                        Engine = scan.Engine,
                        Result = scan.Result,
                        ThreatName = scan.ThreatName,
                        Details = scan.Details
                    });
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

            await _repo.CreateTransferAsync(transfer, savedFiles);

            var downloadLink = Url.Action("Download", "Transfer", new { id = transfer.DownloadToken }, Request.Scheme)
                ?? $"/Transfer/Download/{transfer.DownloadToken}";

            await _email.SendTransferCreatedAsync(transfer, savedFiles, downloadLink);

            await _audit.WriteAsync(
                transfer.TransferId,
                senderEmail,
                "Transfer Created",
                $"Recipient={recipientEmail}; Files={savedFiles.Count}; Subject={subject}",
                HttpContext.Connection.RemoteIpAddress?.ToString());

            ViewBag.DownloadLink = downloadLink;
            return View("Created", transfer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload failed.");
            ModelState.AddModelError("", ex.Message);
            return View();
        }
    }

    [AllowAnonymous]
    [HttpGet("/Transfer/Download/{id}")]
    public async Task<IActionResult> Download(string id)
    {
        var result = await _repo.GetByTokenAsync(id);

        if (result.transfer is null)
        {
            return NotFound("Transfer not found.");
        }

        if (result.transfer.Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("This link has been disabled.");
        }

        if (result.transfer.ExpirationDate < DateTime.UtcNow)
        {
            return BadRequest("This link has expired.");
        }

        return View(new DownloadViewModel
        {
            Transfer = result.transfer,
            Files = result.files
        });
    }

    [AllowAnonymous]
    [HttpGet("/Transfer/File/{token}/{fileId:guid}")]
    public async Task<IActionResult> File(string token, Guid fileId)
    {
        var result = await _repo.GetByTokenAsync(token);

        if (result.transfer is null || result.transfer.ExpirationDate < DateTime.UtcNow)
        {
            return NotFound();
        }

        if (result.transfer.Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("This link has been disabled.");
        }

        var file = result.files.FirstOrDefault(f => f.FileId == fileId);

        if (file is null || !System.IO.File.Exists(file.StoragePath))
        {
            return NotFound();
        }

        await _repo.MarkDownloadedAsync(result.transfer.TransferId);

        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        await _audit.WriteAsync(
            result.transfer.TransferId,
            result.transfer.RecipientEmail,
            "File Downloaded",
            file.OriginalFileName,
            remoteIp);

        await _email.SendDownloadNotificationAsync(result.transfer, result.files, file.OriginalFileName, remoteIp);

        return PhysicalFile(file.StoragePath, file.ContentType ?? "application/octet-stream", file.OriginalFileName);
    }

    [AllowAnonymous]
    [HttpGet("/Transfer/DownloadAll/{token}")]
    public async Task<IActionResult> DownloadAll(string token)
    {
        var result = await _repo.GetByTokenAsync(token);

        if (result.transfer is null || result.transfer.ExpirationDate < DateTime.UtcNow)
        {
            return NotFound();
        }

        if (result.transfer.Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("This link has been disabled.");
        }

        var safeSubject = string.IsNullOrWhiteSpace(result.transfer.Subject)
            ? "FileDrop"
            : string.Concat(result.transfer.Subject.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));

        var zipName = $"{safeSubject}-{DateTime.Now:yyyyMMdd-HHmm}.zip";

        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        await _audit.WriteAsync(
            result.transfer.TransferId,
            result.transfer.RecipientEmail,
            "Download All ZIP",
            $"Files={result.files.Count}; Zip={zipName}",
            remoteIp);

        await _repo.MarkDownloadedAsync(result.transfer.TransferId);

        await _email.SendDownloadNotificationAsync(result.transfer, result.files, $"Download All ZIP: {zipName}", remoteIp);

        return new FileCallbackResult("application/zip", async (output, _) =>
        {
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

            foreach (var file in result.files)
            {
                if (!System.IO.File.Exists(file.StoragePath))
                {
                    continue;
                }

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

    private static string MakeUniqueName(ZipArchive archive, string fileName)
    {
        var clean = string.IsNullOrWhiteSpace(fileName) ? "file.bin" : Path.GetFileName(fileName);

        if (archive.GetEntry(clean) is null)
        {
            return clean;
        }

        var name = Path.GetFileNameWithoutExtension(clean);
        var ext = Path.GetExtension(clean);

        for (var i = 2; i < 9999; i++)
        {
            var candidate = $"{name} ({i}){ext}";
            if (archive.GetEntry(candidate) is null)
            {
                return candidate;
            }
        }

        return $"{Guid.NewGuid():N}-{clean}";
    }
}

public sealed class FileCallbackResult : FileResult
{
    private readonly Func<Stream, ActionContext, Task> _callback;

    public FileCallbackResult(string contentType, Func<Stream, ActionContext, Task> callback)
        : base(contentType)
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




