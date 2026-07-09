using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[Authorize]
public sealed class ChunkedUploadController : Controller
{
    private static readonly string[] DefaultBlockedExtensions =
    {
        ".ade", ".adp", ".apk", ".app", ".appx", ".appxbundle", ".bat", ".cab", ".chm", ".cmd", ".com", ".cpl",
        ".dll", ".dmg", ".exe", ".gadget", ".hta", ".ins", ".iso", ".isp", ".jar", ".jnlp", ".js", ".jse",
        ".lib", ".lnk", ".mde", ".msc", ".msi", ".msix", ".msixbundle", ".msp", ".mst", ".osx", ".pif",
        ".ps1", ".ps1xml", ".ps2", ".ps2xml", ".psc1", ".psc2", ".psd1", ".psm1", ".reg", ".scr", ".sh",
        ".sys", ".vb", ".vbe", ".vbs", ".vxd", ".ws", ".wsc", ".wsf", ".wsh"
    };

    private readonly IChunkedUploadService _chunks;
    private readonly IConfiguration _config;
    private readonly ILogger<ChunkedUploadController> _logger;

    public ChunkedUploadController(
        IChunkedUploadService chunks,
        IConfiguration config,
        ILogger<ChunkedUploadController> logger)
    {
        _chunks = chunks;
        _config = config;
        _logger = logger;
    }

    [HttpPost("/Upload/Start")]
    public async Task<IActionResult> Start([FromBody] StartChunkedUploadRequest request)
    {
        try
        {
            var blockedMessage = GetBlockedFileMessage(request.FileName);
            if (!string.IsNullOrWhiteSpace(blockedMessage))
            {
                _logger.LogWarning("Blocked upload attempt. FileName={FileName}; RemoteIp={RemoteIp}",
                    request.FileName,
                    HttpContext.Connection.RemoteIpAddress?.ToString());

                return BadRequest(new
                {
                    error = blockedMessage,
                    code = "blocked_file_type"
                });
            }

            return Json(await _chunks.StartAsync(HttpContext, request));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start chunked upload for {FileName}", request.FileName);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("/Upload/Chunk")]
    [RequestSizeLimit(60L * 1024L * 1024L)]
    [RequestFormLimits(MultipartBodyLengthLimit = 60L * 1024L * 1024L)]
    public async Task<IActionResult> Chunk(Guid uploadId, int chunkIndex, string? chunkSha256, IFormFile chunk, CancellationToken cancellationToken)
    {
        try
        {
            if (chunk is null || chunk.Length == 0)
            {
                return BadRequest(new { error = "Chunk is missing." });
            }

            return Json(await _chunks.SaveChunkAsync(uploadId, chunkIndex, chunk, chunkSha256, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chunk upload failed. UploadId={UploadId}; ChunkIndex={ChunkIndex}", uploadId, chunkIndex);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("/Upload/Complete")]
    public async Task<IActionResult> Complete([FromBody] CompleteChunkedUploadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Json(await _chunks.CompleteAsync(request.UploadId, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not complete chunked upload. UploadId={UploadId}", request.UploadId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("/Upload/Cancel")]
    public async Task<IActionResult> Cancel([FromBody] CancelChunkedUploadRequest request)
    {
        try
        {
            return Json(await _chunks.CancelAsync(HttpContext, request.UploadId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not cancel chunked upload. UploadId={UploadId}", request.UploadId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("/Upload/Active")]
    public async Task<IActionResult> Active()
    {
        return Json(await _chunks.GetActiveAsync(HttpContext));
    }

    [HttpGet("/Upload/Status/{uploadId:guid}")]
    public async Task<IActionResult> Status(Guid uploadId)
    {
        var status = await _chunks.GetStatusAsync(uploadId);
        return status is null ? NotFound() : Json(status);
    }

    private string? GetBlockedFileMessage(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "File name is required.";

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension)) return null;

        var blocked = GetBlockedExtensions();
        if (!blocked.Contains(extension)) return null;

        return $"{Path.GetFileName(fileName)} is not allowed. Files with the {extension} extension cannot be uploaded.";
    }

    private HashSet<string> GetBlockedExtensions()
    {
        var configured = _config["Uploads:BlockedExtensions"]
            ?? _config["Security:BlockedUploadExtensions"]
            ?? string.Empty;

        var configuredItems = configured
            .Split(',', ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x));

        return DefaultBlockedExtensions
            .Concat(configuredItems!)
            .Select(NormalizeExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeExtension(string value)
    {
        value = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.StartsWith('.') ? value : "." + value;
    }
}
