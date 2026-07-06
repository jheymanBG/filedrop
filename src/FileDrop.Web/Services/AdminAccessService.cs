using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.SqlClient;

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
    private readonly string _connectionString;

    public AdminAccessService(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task<bool> HasAccessAsync(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var email = GetEmail(context.User);
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        await using var db = new SqlConnection(_connectionString);

        var assignedCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.FileDropAdminUsers WHERE IsActive = 1");

        if (assignedCount == 0)
        {
            return true;
        }

        var isAdmin = await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM dbo.FileDropAdminUsers
            WHERE IsActive = 1
              AND LOWER(Email) = LOWER(@email)
            """, new { email });

        return isAdmin > 0;
    }

    public Task GrantAccessAsync(HttpContext context) => Task.CompletedTask;

    public Task<bool> VerifyKeyAsync(string? key) => Task.FromResult(false);

    public Task SignOutAsync(HttpContext context) => Task.CompletedTask;

    private static string? GetEmail(ClaimsPrincipal user)
    {
        return user.FindFirstValue("preferred_username") ??
               user.FindFirstValue(ClaimTypes.Email) ??
               user.FindFirstValue("upn") ??
               user.Identity?.Name;
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireAdminAccessAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;

        if (http.User?.Identity?.IsAuthenticated != true)
        {
            var returnUrl = http.Request.Path + http.Request.QueryString;
            context.Result = new ChallengeResult(OpenIdConnectDefaults.AuthenticationScheme, new Microsoft.AspNetCore.Authentication.AuthenticationProperties
            {
                RedirectUri = returnUrl
            });
            return;
        }

        var access = http.RequestServices.GetRequiredService<IAdminAccessService>();

        if (!await access.HasAccessAsync(http))
        {
            context.Result = new RedirectToActionResult("AccessDenied", "AdminAccess", null);
        }
    }
}
