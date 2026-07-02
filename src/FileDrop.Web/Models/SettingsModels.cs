namespace FileDrop.Web.Models;

public sealed class AppSettingRecord
{
    public string SettingKey { get; set; } = "";
    public string? SettingValue { get; set; }
    public string? Description { get; set; }
    public DateTime ModifiedDate { get; set; }
}

public sealed class SettingsViewModel
{
    public List<AppSettingRecord> Settings { get; set; } = new();
}

public sealed class HealthCheckViewModel
{
    public bool DatabaseOk { get; set; }
    public bool StorageOk { get; set; }
    public bool EmailConfigured { get; set; }
    public string DatabaseMessage { get; set; } = "";
    public string StorageMessage { get; set; } = "";
    public string EmailMessage { get; set; } = "";
    public long StorageBytes { get; set; }
    public string StorageRoot { get; set; } = "";
    public DateTime CheckedAt { get; set; } = DateTime.Now;
}
