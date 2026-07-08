using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[Authorize]
public sealed class ChunkedUploadController : Controller
{
    private readonly IChunkedUploadService _chunks;
    private readonly ILogger<ChunkedUploadController> _logger;

    public ChunkedUploadController(IChunkedUploadService chunks, ILogger<ChunkedUploadController> logger)
    {
        _chunks = chunks;
        _logger = logger;
    }

    [HttpPost("/Upload/Start")]
    public async Task<IActionResult> Start([FromBody] StartChunkedUploadRequest request)
    {
        try
        {
            var result = await _chunks.StartAsync(HttpContext, request);
            return Json(ToStartDto(result));
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

            var result = await _chunks.SaveChunkAsync(uploadId, chunkIndex, chunk, chunkSha256, cancellationToken);
            return Json(ToStatusDto(result, chunkIndex, result.AlreadyReceived));
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
            var result = await _chunks.CompleteAsync(request.UploadId, cancellationToken);
            return Json(ToStatusDto(result));
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
            var result = await _chunks.CancelAsync(HttpContext, request.UploadId);
            return Json(ToStatusDto(result));
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
        return status is null ? NotFound() : Json(ToStatusDto(status));
    }

    private static object ToStartDto(StartChunkedUploadResponse result) => new
    {
        uploadId = result.UploadId,
        totalChunks = result.TotalChunks,
        chunkSizeBytes = result.ChunkSizeBytes,
        status = result.Status,
        completedChunks = result.CompletedChunks,
        bytesReceived = result.BytesReceived,
        totalBytes = result.TotalBytes,
        percent = result.Percent,
        alreadyComplete = result.AlreadyComplete
    };

    private static object ToStatusDto(ChunkedUploadStatus result, int? chunkIndex = null, bool? alreadyReceived = null) => new
    {
        uploadId = result.UploadId,
        status = result.Status,
        chunksReceived = result.ChunksReceived,
        totalChunks = result.TotalChunks,
        completedChunks = result.CompletedChunks,
        bytesReceived = result.BytesReceived,
        totalBytes = result.TotalBytes,
        percent = result.Percent,
        originalFileName = result.OriginalFileName,
        storedFileName = result.StoredFileName,
        storagePath = result.StoragePath,
        sha256Hash = result.Sha256Hash,
        expectedSha256Hash = result.ExpectedSha256Hash,
        clientFileId = result.ClientFileId,
        createdDate = result.CreatedDate,
        completedDate = result.CompletedDate,
        lastActivityDate = result.LastActivityDate,
        chunkIndex,
        alreadyReceived
    };
}
