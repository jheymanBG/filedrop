namespace FileDrop.Web.Models;

public sealed class EntraValidationItem
{
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string Details { get; set; } = "";
    public bool Passed => Status.Equals("Pass", StringComparison.OrdinalIgnoreCase);
    public bool Warning => Status.Equals("Warning", StringComparison.OrdinalIgnoreCase);
}

public sealed class EntraValidationViewModel
{
    public DateTime CheckedAt { get; set; } = DateTime.Now;
    public List<EntraValidationItem> Items { get; set; } = new();
    public int PassedCount => Items.Count(x => x.Passed);
    public int WarningCount => Items.Count(x => x.Warning);
    public int FailedCount => Items.Count(x => x.Status.Equals("Fail", StringComparison.OrdinalIgnoreCase));
    public bool CanSwitchToMicrosoftLogin { get; set; }
    public string? LastSuccessfulMicrosoftLogin { get; set; }
}
