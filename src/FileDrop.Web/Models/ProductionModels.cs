namespace FileDrop.Web.Models;

public sealed class ProductionChecklistItem
{
    public int ChecklistId { get; set; }
    public string Category { get; set; } = "";
    public string Item { get; set; } = "";
    public bool IsComplete { get; set; }
    public string? Notes { get; set; }
    public int SortOrder { get; set; }
    public DateTime ModifiedDate { get; set; }
}

public sealed class ProductionReadinessViewModel
{
    public List<ProductionChecklistItem> Items { get; set; } = new();
    public int CompleteCount => Items.Count(x => x.IsComplete);
    public int TotalCount => Items.Count;
    public decimal PercentComplete => TotalCount == 0 ? 0 : Math.Round((decimal)CompleteCount / TotalCount * 100, 1);
}
