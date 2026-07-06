using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

public sealed class AdminAccessController : Controller
{
    [HttpGet("/Admin/Login")]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return Redirect(string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl) ? "/Admin" : returnUrl);
        }

        return Challenge(OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpPost("/Admin/Login")]
    public IActionResult LoginPost(string? returnUrl = null)
    {
        return Challenge(OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpGet("/Admin/AccessDenied")]
    public IActionResult AccessDenied()
    {
        return View();
    }

    [HttpPost("/Admin/Logout")]
    public IActionResult Logout()
    {
        return Redirect("/");
    }
}
