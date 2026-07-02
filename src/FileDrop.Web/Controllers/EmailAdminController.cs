using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class EmailAdminController : Controller
{
    private readonly IEmailTestService _emailTest;
    private readonly IAuditRepository _audit;
    private readonly IConfiguration _config;

    public EmailAdminController(IEmailTestService emailTest, IAuditRepository audit, IConfiguration config)
    {
        _emailTest = emailTest;
        _audit = audit;
        _config = config;
    }

    [HttpGet("/Admin/Email")]
    public IActionResult Index()
    {
        return View(new EmailTestViewModel());
    }

    [HttpPost("/Admin/Email/Test")]
    public async Task<IActionResult> Test(EmailTestViewModel model)
    {
        if (string.IsNullOrWhiteSpace(model.ToEmail))
        {
            model.Success = false;
            model.Result = "Enter a recipient email address.";
            return View("Index", model);
        }

        try
        {
            await _emailTest.SendTestAsync(model.ToEmail);
            await _audit.WriteAsync(null, User?.Identity?.Name, "SMTP Test Sent", model.ToEmail, HttpContext.Connection.RemoteIpAddress?.ToString());

            model.Success = true;
            model.Result = $"Test email sent to {model.ToEmail}.";
        }
        catch (Exception ex)
        {
            await _audit.WriteAsync(null, User?.Identity?.Name, "SMTP Test Failed", $"{model.ToEmail}; {ex.Message}", HttpContext.Connection.RemoteIpAddress?.ToString());

            model.Success = false;
            model.Result = ex.Message;
        }

        return View("Index", model);
    }
}
