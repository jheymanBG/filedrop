using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class AdminUsersController : Controller
{
    private readonly IAdminUserRepository _users;
    private readonly IAuditRepository _audit;

    public AdminUsersController(IAdminUserRepository users, IAuditRepository audit)
    {
        _users = users;
        _audit = audit;
    }

    [HttpGet("/Admin/AdminUsers")]
    public async Task<IActionResult> Index()
    {
        return View(new AdminUsersViewModel { Users = await _users.GetAllAsync() });
    }

    [HttpPost("/Admin/AdminUsers/Add")]
    public async Task<IActionResult> Add(string email, string? displayName, string roleName = "Administrator")
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            TempData["Message"] = "Email is required.";
            return RedirectToAction(nameof(Index));
        }

        await _users.AddAsync(email.Trim(), displayName?.Trim(), string.IsNullOrWhiteSpace(roleName) ? "Administrator" : roleName.Trim());
        await _audit.WriteAsync(null, User?.Identity?.Name, "Admin User Added", email, HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Message"] = "Admin user saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/AdminUsers/Disable")]
    public async Task<IActionResult> Disable(int adminUserId)
    {
        await _users.DisableAsync(adminUserId);
        await _audit.WriteAsync(null, User?.Identity?.Name, "Admin User Disabled", adminUserId.ToString(), HttpContext.Connection.RemoteIpAddress?.ToString());
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/AdminUsers/Enable")]
    public async Task<IActionResult> Enable(int adminUserId)
    {
        await _users.EnableAsync(adminUserId);
        await _audit.WriteAsync(null, User?.Identity?.Name, "Admin User Enabled", adminUserId.ToString(), HttpContext.Connection.RemoteIpAddress?.ToString());
        return RedirectToAction(nameof(Index));
    }
}
