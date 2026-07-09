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

    public bool EnableLoginFailureAlerts { get; set; } = true;
    public string LoginFailureAlertEmail { get; set; } = "jheyman@bgohio.gov";
    public int LoginFailureAlertThreshold { get; set; } = 3;
    public int LoginFailureWindowMinutes { get; set; } = 15;
    public int LoginFailureAlertCooldownMinutes { get; set; } = 30;

    public bool EnableLowDiskSpaceAlerts { get; set; } = true;
    public string LowDiskSpaceAlertEmails { get; set; } = "jheyman@bgohio.gov";
    public int LowDiskSpaceThresholdPercentFree { get; set; } = 10;
    public int LowDiskSpaceThresholdFreeGb { get; set; } = 25;
    public int LowDiskSpaceCheckIntervalMinutes { get; set; } = 30;
    public int LowDiskSpaceAlertCooldownMinutes { get; set; } = 120;
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
