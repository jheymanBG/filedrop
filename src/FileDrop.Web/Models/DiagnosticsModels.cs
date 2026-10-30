namespace FileDrop.Web.Models;

public sealed class DiagnosticCheckResult
{
    public string Name { get; set; } = "";
    public bool IsHealthy { get; set; }
    public string Status { get; set; } = "";
    public string Details { get; set; } = "";
    public TimeSpan Duration { get; set; }
}

public sealed class SystemDiagnosticsViewModel
{
    public string ProductVersion { get; set; } = "";
    public string CommitHash { get; set; } = "";
    public string BuildTimestamp { get; set; } = "";
    public string EnvironmentName { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public string RuntimeVersion { get; set; } = "";
    public string DatabaseSchemaVersion { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public TimeSpan Uptime { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public List<DiagnosticCheckResult> Checks { get; set; } = new();
}
