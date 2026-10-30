namespace FileDrop.Web.Models;

public sealed class PiiAdminViewModel
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "Warn";
    public string Sensitivity { get; set; } = "Balanced";
    public string LowConfidenceAction { get; set; } = "AllowLog";
    public string MediumConfidenceAction { get; set; } = "Warn";
    public bool BlockOnFailure { get; set; }
    public bool BlockUnscannable { get; set; }
    public bool ScanContactInfo { get; set; }
    public int MaxExtractedCharacters { get; set; }
    public int MaxPdfPages { get; set; }
    public int MaxArchiveExpandedMB { get; set; }
    public HashSet<string> EnabledCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PiiScanRecord> RecentScans { get; set; } = new();

    public string Provider { get; set; } = "Auto";
    public string PresidioUrl { get; set; } = "http://127.0.0.1:5102";
    public string PresidioFallback { get; set; } = "BuiltIn";
    public double PresidioLowThreshold { get; set; } = 0.35;
    public double PresidioMediumThreshold { get; set; } = 0.60;
    public double PresidioHighThreshold { get; set; } = 0.85;
    public bool PresidioHealthy { get; set; }
    public string PresidioHealthMessage { get; set; } = "Not checked";
    public string EffectiveProvider { get; set; } = "BuiltIn";
}
