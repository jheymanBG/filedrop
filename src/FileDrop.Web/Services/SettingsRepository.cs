using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface ISettingsRepository
{
    Task<List<AppSettingRecord>> GetAllAsync();
    Task UpdateAsync(string key, string? value);
    Task<string?> GetValueAsync(string key);
    Task<int> GetIntAsync(string key, int defaultValue);
}

public sealed class SettingsRepository : ISettingsRepository
{
    private readonly string _connectionString;

    public SettingsRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task<List<AppSettingRecord>> GetAllAsync()
    {
        await using var db = new SqlConnection(_connectionString);
        return (await db.QueryAsync<AppSettingRecord>("SELECT * FROM dbo.AppSettings ORDER BY SettingKey")).ToList();
    }

    public async Task UpdateAsync(string key, string? value)
    {
        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync("""
            UPDATE dbo.AppSettings
            SET SettingValue = @value,
                ModifiedDate = SYSUTCDATETIME()
            WHERE SettingKey = @key
            """, new { key, value });
    }

    public async Task<string?> GetValueAsync(string key)
    {
        await using var db = new SqlConnection(_connectionString);
        return await db.ExecuteScalarAsync<string?>("SELECT SettingValue FROM dbo.AppSettings WHERE SettingKey = @key", new { key });
    }

    public async Task<int> GetIntAsync(string key, int defaultValue)
    {
        var value = await GetValueAsync(key);
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }
}
