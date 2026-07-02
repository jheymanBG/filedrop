using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class SecurityController : Controller
{
    private readonly IFileScanRepository _scans;

    public SecurityController(IFileScanRepository scans)
    {
        _scans = scans;
    }

    [HttpGet("/Admin/Security")]
    public async Task<IActionResult> Index()
    {
        return View(await _scans.GetDashboardAsync());
    }
}
