using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class DeploymentController : Controller
{
    private readonly IAuditRepository _audit;

    public DeploymentController(IAuditRepository audit)
    {
        _audit = audit;
    }

    [HttpGet("/Admin/Deployment")]
    public IActionResult Index()
    {
        var root = "C:\\Build\\FileDrop_v1_starter";
        var backups = Path.Combine(root, "Backups");

        var list = new List<DirectoryInfo>();

        if (Directory.Exists(backups))
        {
            list = Directory.GetDirectories(backups)
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.Name)
                .Take(25)
                .ToList();
        }

        return View(list);
    }

    [HttpPost("/Admin/Deployment/Backup")]
    public async Task<IActionResult> Backup()
    {
        await _audit.WriteAsync(null, User?.Identity?.Name, "Manual Backup Requested", null, HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = "Run Backup-FileDrop.ps1 on the server to create a full backup.";
        return RedirectToAction(nameof(Index));
    }
}
