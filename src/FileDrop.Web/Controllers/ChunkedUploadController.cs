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

    [HttpGet("/Upload/Status/{uploadId:guid}")]
    public async Task<IActionResult> Status(Guid uploadId)
    {
        var status = await _chunks.GetStatusAsync(uploadId);
        return status is null ? NotFound() : Json(status);
    }
}
