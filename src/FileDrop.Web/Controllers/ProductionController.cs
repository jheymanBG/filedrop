using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class ProductionController : Controller
{
    private readonly IProductionRepository _production;
    private readonly IAuditRepository _audit;

    public ProductionController(IProductionRepository production, IAuditRepository audit)
    {
        _production = production;
        _audit = audit;
    }

    [HttpGet("/Admin/Production")]
    public async Task<IActionResult> Index()
    {
        return View(await _production.GetAsync());
    }

    [HttpPost("/Admin/Production/Update")]
    public async Task<IActionResult> Update(int checklistId, bool isComplete, string? notes)
    {
        await _production.UpdateAsync(checklistId, isComplete, notes);
        await _audit.WriteAsync(null, User?.Identity?.Name, "Production Checklist Updated", $"ChecklistId={checklistId}; Complete={isComplete}", HttpContext.Connection.RemoteIpAddress?.ToString());
        return RedirectToAction(nameof(Index));
    }
}
