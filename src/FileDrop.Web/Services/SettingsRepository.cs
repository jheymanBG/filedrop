using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface ISettingsRepository
{
    Task<List<AppSettingRecord>> GetAllAsync();
    Task UpdateAsync(string key, string? value);
    Task UpsertAsync(string key, string? value, string? description = null);
    Task<string?> GetValueAsync(string key);
    Task<string> GetStringAsync(string key, string defaultValue);
    Task<int> GetIntAsync(string key, int defaultValue);
    Task<bool> GetBoolAsync(string key, bool defaultValue);
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
        await UpsertAsync(key, value);
    }

    public async Task UpsertAsync(string key, string? value, string? description = null)
    {
        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync("""
            IF EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = @key)
            BEGIN
                UPDATE dbo.AppSettings
                SET SettingValue = @value,
                    Description = COALESCE(@description, Description),
                    ModifiedDate = SYSUTCDATETIME()
                WHERE SettingKey = @key;
            END
            ELSE
            BEGIN
                INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description, ModifiedDate)
                VALUES (@key, @value, @description, SYSUTCDATETIME());
            END
            """, new { key, value, description });
    }

    public async Task<string?> GetValueAsync(string key)
    {
        await using var db = new SqlConnection(_connectionString);
        return await db.ExecuteScalarAsync<string?>("SELECT SettingValue FROM dbo.AppSettings WHERE SettingKey = @key", new { key });
    }

    public async Task<string> GetStringAsync(string key, string defaultValue)
    {
        var value = await GetValueAsync(key);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    public async Task<int> GetIntAsync(string key, int defaultValue)
    {
        var value = await GetValueAsync(key);
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue)
    {
        var value = await GetValueAsync(key);
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (bool.TryParse(value, out var parsed)) return parsed;
        if (int.TryParse(value, out var i)) return i != 0;
        return defaultValue;
    }
}
