using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class ReportsController : Controller
{
    private readonly IReportRepository _reports;
    private readonly IAuditRepository _audit;

    public ReportsController(IReportRepository reports, IAuditRepository audit)
    {
        _reports = reports;
        _audit = audit;
    }

    [HttpGet("/Admin/Reports")]
    public IActionResult Index()
    {
        return View(new ReportsIndexViewModel());
    }

    [HttpGet("/Admin/Reports/Transfers.csv")]
    public async Task<IActionResult> Transfers()
    {
        await _audit.WriteAsync(null, User?.Identity?.Name, "Report Export", "Transfers.csv", HttpContext.Connection.RemoteIpAddress?.ToString());
        return Csv(await _reports.ExportTransfersCsvAsync(), "FileDrop-Transfers.csv");
    }

    [HttpGet("/Admin/Reports/Files.csv")]
    public async Task<IActionResult> Files()
    {
        await _audit.WriteAsync(null, User?.Identity?.Name, "Report Export", "Files.csv", HttpContext.Connection.RemoteIpAddress?.ToString());
        return Csv(await _reports.ExportFilesCsvAsync(), "FileDrop-Files.csv");
    }

    [HttpGet("/Admin/Reports/Audit.csv")]
    public async Task<IActionResult> Audit()
    {
        await _audit.WriteAsync(null, User?.Identity?.Name, "Report Export", "Audit.csv", HttpContext.Connection.RemoteIpAddress?.ToString());
        return Csv(await _reports.ExportAuditCsvAsync(), "FileDrop-Audit.csv");
    }

    [HttpGet("/Admin/Reports/Storage.csv")]
    public async Task<IActionResult> Storage()
    {
        await _audit.WriteAsync(null, User?.Identity?.Name, "Report Export", "Storage.csv", HttpContext.Connection.RemoteIpAddress?.ToString());
        return Csv(await _reports.ExportStorageSummaryCsvAsync(), "FileDrop-Storage.csv");
    }

    private static FileContentResult Csv(string text, string filename)
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        return new FileContentResult(bytes, "text/csv; charset=utf-8")
        {
            FileDownloadName = filename
        };
    }
}
