using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

public sealed class AdminAccessController : Controller
{
    private readonly IAdminAccessService _access;
    private readonly IAuditRepository _audit;

    public AdminAccessController(IAdminAccessService access, IAuditRepository audit)
    {
        _access = access;
        _audit = audit;
    }

    [HttpGet("/Admin/Login")]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewBag.ReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/Admin" : returnUrl;
        return View();
    }

    [HttpPost("/Admin/Login")]
    public async Task<IActionResult> Login(string key, string? returnUrl = null)
    {
        if (!await _access.VerifyKeyAsync(key))
        {
            ModelState.AddModelError("", "Invalid admin access key.");
            ViewBag.ReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/Admin" : returnUrl;
            await _audit.WriteAsync(null, User?.Identity?.Name, "Admin Login Failed", null, HttpContext.Connection.RemoteIpAddress?.ToString());
            return View();
        }

        await _access.GrantAccessAsync(HttpContext);
        await _audit.WriteAsync(null, User?.Identity?.Name, "Admin Login", null, HttpContext.Connection.RemoteIpAddress?.ToString());

        if (string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl))
        {
            returnUrl = "/Admin";
        }

        return Redirect(returnUrl);
    }

    [HttpPost("/Admin/Logout")]
    public async Task<IActionResult> Logout()
    {
        await _access.SignOutAsync(HttpContext);
        await _audit.WriteAsync(null, User?.Identity?.Name, "Admin Logout", null, HttpContext.Connection.RemoteIpAddress?.ToString());
        return RedirectToAction(nameof(Login));
    }
}
