using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IFileScanRepository
{
    Task AddAsync(FileScanRecord record);
    Task<SecurityDashboardViewModel> GetDashboardAsync();
}

public sealed class FileScanRepository : IFileScanRepository
{
    private readonly string _connectionString;

    public FileScanRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task AddAsync(FileScanRecord record)
    {
        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync("""
            INSERT INTO dbo.FileScanResults
            (TransferId, FileId, OriginalFileName, StoragePath, Sha256Hash, Engine, Result, ThreatName, Details)
            VALUES
            (@TransferId, @FileId, @OriginalFileName, @StoragePath, @Sha256Hash, @Engine, @Result, @ThreatName, @Details)
            """, record);
    }

    public async Task<SecurityDashboardViewModel> GetDashboardAsync()
    {
        await using var db = new SqlConnection(_connectionString);

        var model = new SecurityDashboardViewModel
        {
            TotalScans = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.FileScanResults"),
            CleanCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.FileScanResults WHERE Result = 'Clean'"),
            QuarantinedCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.FileScanResults WHERE Result = 'Quarantined'"),
            FailedCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.FileScanResults WHERE Result = 'Failed'"),
            InfectedCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.FileScanResults WHERE Result = 'Infected'")
        };

        model.RecentScans = (await db.QueryAsync<FileScanRecord>("""
            SELECT TOP 100 *
            FROM dbo.FileScanResults
            ORDER BY ScannedDate DESC
            """)).ToList();

        return model;
    }
}
