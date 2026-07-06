using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IAdminUserRepository
{
    Task<List<FileDropAdminUser>> GetAllAsync();
    Task AddAsync(string email, string? displayName, string roleName);
    Task DisableAsync(int adminUserId);
    Task EnableAsync(int adminUserId);
}

public sealed class AdminUserRepository : IAdminUserRepository
{
    private readonly string _connectionString;

    public AdminUserRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task<List<FileDropAdminUser>> GetAllAsync()
    {
        await using var db = new SqlConnection(_connectionString);
        return (await db.QueryAsync<FileDropAdminUser>("""
            SELECT *
            FROM dbo.FileDropAdminUsers
            ORDER BY IsActive DESC, Email
            """)).ToList();
    }

    public async Task AddAsync(string email, string? displayName, string roleName)
    {
        await using var db = new SqlConnection(_connectionString);

        await db.ExecuteAsync("""
            IF EXISTS (SELECT 1 FROM dbo.FileDropAdminUsers WHERE LOWER(Email) = LOWER(@email))
            BEGIN
                UPDATE dbo.FileDropAdminUsers
                SET DisplayName = @displayName,
                    RoleName = @roleName,
                    IsActive = 1,
                    ModifiedDate = SYSUTCDATETIME()
                WHERE LOWER(Email) = LOWER(@email);
            END
            ELSE
            BEGIN
                INSERT INTO dbo.FileDropAdminUsers (Email, DisplayName, RoleName, IsActive)
                VALUES (@email, @displayName, @roleName, 1);
            END
            """, new { email, displayName, roleName });
    }

    public async Task DisableAsync(int adminUserId)
    {
        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync("""
            UPDATE dbo.FileDropAdminUsers
            SET IsActive = 0,
                ModifiedDate = SYSUTCDATETIME()
            WHERE AdminUserId = @adminUserId
            """, new { adminUserId });
    }

    public async Task EnableAsync(int adminUserId)
    {
        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync("""
            UPDATE dbo.FileDropAdminUsers
            SET IsActive = 1,
                ModifiedDate = SYSUTCDATETIME()
            WHERE AdminUserId = @adminUserId
            """, new { adminUserId });
    }
}
