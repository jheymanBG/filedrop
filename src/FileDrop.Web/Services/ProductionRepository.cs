using Dapper;
using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IProductionRepository
{
    Task<ProductionReadinessViewModel> GetAsync();
    Task UpdateAsync(int checklistId, bool isComplete, string? notes);
}

public sealed class ProductionRepository : IProductionRepository
{
    private readonly string _connectionString;

    public ProductionRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task<ProductionReadinessViewModel> GetAsync()
    {
        await using var db = new SqlConnection(_connectionString);
        var items = (await db.QueryAsync<ProductionChecklistItem>("SELECT * FROM dbo.ProductionChecklist ORDER BY SortOrder, ChecklistId")).ToList();
        return new ProductionReadinessViewModel { Items = items };
    }

    public async Task UpdateAsync(int checklistId, bool isComplete, string? notes)
    {
        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync("""
            UPDATE dbo.ProductionChecklist
            SET IsComplete = @isComplete, Notes = @notes, ModifiedDate = SYSUTCDATETIME()
            WHERE ChecklistId = @checklistId
            """, new { checklistId, isComplete, notes });
    }
}
