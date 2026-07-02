using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IAuditRepository
{
    Task WriteAsync(Guid? transferId, string? userEmail, string action, string? details, string? ipAddress);
    Task<List<AuditRecord>> SearchAsync(string? query);
}

public sealed class AuditRepository : IAuditRepository
{
    private readonly string _connectionString;

    public AuditRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task WriteAsync(Guid? transferId, string? userEmail, string action, string? details, string? ipAddress)
    {
        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync("""
            INSERT INTO dbo.AuditLog (TransferId, UserEmail, Action, Details, IpAddress)
            VALUES (@transferId, @userEmail, @action, @details, @ipAddress)
            """, new { transferId, userEmail, action, details, ipAddress });
    }

    public async Task<List<AuditRecord>> SearchAsync(string? query)
    {
        await using var db = new SqlConnection(_connectionString);

        return (await db.QueryAsync<AuditRecord>("""
            SELECT TOP 500 *
            FROM dbo.AuditLog
            WHERE
                @query IS NULL OR @query = '' OR
                UserEmail LIKE '%' + @query + '%' OR
                Action LIKE '%' + @query + '%' OR
                Details LIKE '%' + @query + '%'
            ORDER BY CreatedDate DESC
            """, new { query })).ToList();
    }
}
