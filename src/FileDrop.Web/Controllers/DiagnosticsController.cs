using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class DiagnosticsController : Controller
{
    private readonly IDiagnosticsService _diagnostics;
    private readonly IAuditRepository _audit;

    public DiagnosticsController(IDiagnosticsService diagnostics, IAuditRepository audit)
    {
        _diagnostics = diagnostics;
        _audit = audit;
    }

    [HttpGet("/Admin/Diagnostics")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
        => View(await _diagnostics.RunAsync(cancellationToken));

    [HttpGet("/Admin/Diagnostics/Download")]
    public async Task<IActionResult> Download(CancellationToken cancellationToken)
    {
        var bytes = await _diagnostics.CreateSupportPackageAsync(cancellationToken);
        await _audit.WriteAsync(null, User?.Identity?.Name, "Diagnostics Package Created", null, HttpContext.Connection.RemoteIpAddress?.ToString());
        return File(bytes, "application/zip", $"FileDrop-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
    }
}
