using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FileDrop.Web.Services;

public interface IAdminAccessService
{
    Task<bool> HasAccessAsync(HttpContext context);
    Task GrantAccessAsync(HttpContext context);
    Task<bool> VerifyKeyAsync(string? key);
    Task SignOutAsync(HttpContext context);
}

public sealed class AdminAccessService : IAdminAccessService
{
    private const string SessionCookie = "FileDropAdminAccess";
    private readonly ISettingsRepository _settings;

    public AdminAccessService(ISettingsRepository settings)
    {
        _settings = settings;
    }

    public async Task<bool> HasAccessAsync(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            // Once Entra is approved, authenticated users can be combined with roles/groups.
            // For now, still require the local admin cookie unless the app admin chooses otherwise later.
        }

        if (!context.Request.Cookies.TryGetValue(SessionCookie, out var value))
        {
            return false;
        }

        return await VerifyKeyAsync(value);
    }

    public async Task GrantAccessAsync(HttpContext context)
    {
        var key = await _settings.GetValueAsync("Security.AdminAccessKey");
        var hours = await _settings.GetIntAsync("Security.AdminSessionHours", 8);

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Security.AdminAccessKey is not configured.");
        }

        context.Response.Cookies.Append(SessionCookie, key, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Expires = DateTimeOffset.Now.AddHours(hours)
        });
    }

    public async Task<bool> VerifyKeyAsync(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var configured = await _settings.GetValueAsync("Security.AdminAccessKey");
        return !string.IsNullOrWhiteSpace(configured) &&
               string.Equals(configured, key, StringComparison.Ordinal);
    }

    public Task SignOutAsync(HttpContext context)
    {
        context.Response.Cookies.Delete(SessionCookie);
        return Task.CompletedTask;
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireAdminAccessAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var access = context.HttpContext.RequestServices.GetRequiredService<IAdminAccessService>();

        if (!await access.HasAccessAsync(context.HttpContext))
        {
            var returnUrl = context.HttpContext.Request.Path + context.HttpContext.Request.QueryString;
            context.Result = new RedirectToActionResult("Login", "AdminAccess", new { returnUrl });
        }
    }
}
