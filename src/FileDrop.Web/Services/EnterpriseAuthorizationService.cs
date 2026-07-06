using System.Security.Claims;
using Dapper;
using FileDrop.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IEnterpriseAuthorizationService
{
    Task<bool> HasRoleAsync(HttpContext context, string role);
    Task<List<string>> GetEffectiveRolesAsync(ClaimsPrincipal? user);
    Task<AuthorizationStatusViewModel> GetStatusAsync(HttpContext context);
    Task RecordEventAsync(HttpContext context, string? requiredRole, bool authorized, string? reason);
}

public sealed class EnterpriseAuthorizationService : IEnterpriseAuthorizationService
{
    private readonly ISettingsRepository _settings;
    private readonly string _connectionString;

    public EnterpriseAuthorizationService(ISettingsRepository settings, IConfiguration config)
    {
        _settings = settings;
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task<bool> HasRoleAsync(HttpContext context, string role)
    {
        var roles = await GetEffectiveRolesAsync(context.User);
        var authorized = roles.Contains(role, StringComparer.OrdinalIgnoreCase);

        if (!authorized && role == FileDropRoles.Administrator)
        {
            var requireAdminGroup = (await _settings.GetValueAsync("Authorization.RequireAdminGroupForAdminPortal"))?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

            if (!requireAdminGroup)
            {
                // Temporary admin key remains valid until Entra is approved.
                var adminAccess = context.RequestServices.GetService<IAdminAccessService>();
                if (adminAccess is not null && await adminAccess.HasAccessAsync(context))
                {
                    authorized = true;
                }
            }
        }

        await RecordEventAsync(context, role, authorized, authorized ? "Authorized" : "Missing required role/group");
        return authorized;
    }

    public async Task<List<string>> GetEffectiveRolesAsync(ClaimsPrincipal? user)
    {
        var roles = new List<string>();

        if (user?.Identity?.IsAuthenticated != true)
        {
            return roles;
        }

        roles.Add(FileDropRoles.User);

        var adminGroup = await _settings.GetValueAsync("Authorization.AdminGroupObjectId");
        var auditorGroup = await _settings.GetValueAsync("Authorization.AuditorGroupObjectId");
        var helpDeskGroup = await _settings.GetValueAsync("Authorization.HelpDeskGroupObjectId");

        var groups = GetGroupClaims(user).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(adminGroup) && groups.Contains(adminGroup))
        {
            roles.Add(FileDropRoles.Administrator);
            roles.Add(FileDropRoles.Auditor);
            roles.Add(FileDropRoles.HelpDesk);
        }

        if (!string.IsNullOrWhiteSpace(auditorGroup) && groups.Contains(auditorGroup))
        {
            roles.Add(FileDropRoles.Auditor);
        }

        if (!string.IsNullOrWhiteSpace(helpDeskGroup) && groups.Contains(helpDeskGroup))
        {
            roles.Add(FileDropRoles.HelpDesk);
        }

        return roles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<AuthorizationStatusViewModel> GetStatusAsync(HttpContext context)
    {
        var user = context.User;
        var model = new AuthorizationStatusViewModel
        {
            Mode = await _settings.GetValueAsync("Authorization.Mode") ?? "TemporaryAdminKey",
            AdminGroupObjectId = await _settings.GetValueAsync("Authorization.AdminGroupObjectId") ?? "",
            AuditorGroupObjectId = await _settings.GetValueAsync("Authorization.AuditorGroupObjectId") ?? "",
            HelpDeskGroupObjectId = await _settings.GetValueAsync("Authorization.HelpDeskGroupObjectId") ?? "",
            RequireAdminGroupForAdminPortal = (await _settings.GetValueAsync("Authorization.RequireAdminGroupForAdminPortal"))?.Equals("true", StringComparison.OrdinalIgnoreCase) == true,
            IsSignedIn = user?.Identity?.IsAuthenticated == true,
            UserName = user?.Identity?.Name,
            UserEmail = user?.FindFirstValue("preferred_username") ?? user?.FindFirstValue(ClaimTypes.Email),
            GroupClaims = user is null ? new List<string>() : GetGroupClaims(user).ToList(),
            EffectiveRoles = await GetEffectiveRolesAsync(user)
        };

        await using var db = new SqlConnection(_connectionString);
        model.RecentEvents = (await db.QueryAsync<AuthorizationEventRecord>("""
            SELECT TOP 100 *
            FROM dbo.AuthorizationEvents
            ORDER BY CreatedDate DESC
            """)).ToList();

        return model;
    }

    public async Task RecordEventAsync(HttpContext context, string? requiredRole, bool authorized, string? reason)
    {
        try
        {
            var user = context.User;
            await using var db = new SqlConnection(_connectionString);

            await db.ExecuteAsync("""
                INSERT INTO dbo.AuthorizationEvents
                (UserEmail, UserName, RequiredRole, Authorized, Reason, IpAddress)
                VALUES
                (@UserEmail, @UserName, @RequiredRole, @Authorized, @Reason, @IpAddress)
                """, new
            {
                UserEmail = user?.FindFirstValue("preferred_username") ?? user?.FindFirstValue(ClaimTypes.Email),
                UserName = user?.Identity?.Name,
                RequiredRole = requiredRole,
                Authorized = authorized,
                Reason = reason,
                IpAddress = context.Connection.RemoteIpAddress?.ToString()
            });
        }
        catch
        {
            // Do not break authorization if event logging fails.
        }
    }

    private static IEnumerable<string> GetGroupClaims(ClaimsPrincipal user)
    {
        return user.Claims
            .Where(c =>
                c.Type.Equals("groups", StringComparison.OrdinalIgnoreCase) ||
                c.Type.Equals("http://schemas.microsoft.com/ws/2008/06/identity/claims/groups", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v));
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireFileDropRoleAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly string _role;

    public RequireFileDropRoleAttribute(string role)
    {
        _role = role;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var auth = context.HttpContext.RequestServices.GetRequiredService<IEnterpriseAuthorizationService>();

        if (!await auth.HasRoleAsync(context.HttpContext, _role))
        {
            context.Result = new RedirectToActionResult("AccessDenied", "AuthorizationAdmin", new { role = _role });
        }
    }
}
