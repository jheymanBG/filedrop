using System.Globalization;
using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileDrop.Web.Controllers;

[RequireAdminAccess]
public sealed class PiiAdminController : Controller
{
    private static readonly string[] ValidCategories = ["SSN", "CreditCard", "DateOfBirth", "BankAccount", "Passport", "DriverLicense", "ITIN"];
    private static readonly string[] ValidSensitivity = ["Balanced", "Strict", "Custom"];
    private static readonly string[] ValidActions = ["AllowLog", "Warn", "Block"];
    private static readonly string[] ValidProviders = ["Auto", "Presidio", "BuiltIn", "Disabled"];
    private static readonly string[] ValidFallbacks = ["BuiltIn", "Block", "AllowLog"];

    private readonly ISettingsRepository _settings;
    private readonly IPiiScanRepository _scans;
    private readonly IPresidioPiiClient _presidio;
    private readonly IAuditRepository _audit;

    public PiiAdminController(ISettingsRepository settings, IPiiScanRepository scans, IPresidioPiiClient presidio, IAuditRepository audit)
    {
        _settings = settings;
        _scans = scans;
        _presidio = presidio;
        _audit = audit;
    }

    [HttpGet("/Admin/Pii")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var categories = (await _settings.GetStringAsync("Security.PiiScanning.Categories", "SSN,CreditCard,DateOfBirth,BankAccount,Passport,DriverLicense,ITIN"))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var provider = NormalizeProvider(await _settings.GetStringAsync("Security.PiiScanning.Provider", "Auto"));
        var presidioUrl = await _settings.GetStringAsync("Security.PiiScanning.PresidioUrl", "http://127.0.0.1:5102");
        var health = await _presidio.GetHealthAsync(presidioUrl, cancellationToken);
        var effectiveProvider = provider switch
        {
            "Disabled" => "Disabled",
            "BuiltIn" => "FileDrop Built-In",
            "Presidio" => health.Healthy ? "Microsoft Presidio" : "Presidio unavailable",
            _ => health.Healthy ? "Microsoft Presidio" : "FileDrop Built-In (automatic fallback)"
        };

        return View(new PiiAdminViewModel
        {
            Enabled = await _settings.GetBoolAsync("Security.PiiScanning.Enabled", true),
            Mode = await _settings.GetStringAsync("Security.PiiScanning.Mode", "Warn"),
            Sensitivity = NormalizeSensitivity(await _settings.GetStringAsync("Security.PiiScanning.Sensitivity", "Balanced")),
            LowConfidenceAction = NormalizeAction(await _settings.GetStringAsync("Security.PiiScanning.LowConfidenceAction", "AllowLog"), "AllowLog"),
            MediumConfidenceAction = NormalizeAction(await _settings.GetStringAsync("Security.PiiScanning.MediumConfidenceAction", "Warn"), "Warn"),
            BlockOnFailure = await _settings.GetBoolAsync("Security.PiiScanning.BlockOnFailure", true),
            BlockUnscannable = await _settings.GetBoolAsync("Security.PiiScanning.BlockUnscannable", false),
            ScanContactInfo = await _settings.GetBoolAsync("Security.PiiScanning.ScanContactInfo", false),
            MaxExtractedCharacters = Math.Clamp(await _settings.GetIntAsync("Security.PiiScanning.MaxExtractedCharacters", 2_000_000), 10_000, 10_000_000),
            MaxPdfPages = Math.Clamp(await _settings.GetIntAsync("Security.PiiScanning.MaxPdfPages", 250), 1, 5000),
            MaxArchiveExpandedMB = Math.Clamp(await _settings.GetIntAsync("Security.PiiScanning.MaxArchiveExpandedMB", 100), 1, 2048),
            EnabledCategories = categories,
            RecentScans = await _scans.GetRecentAsync(50),
            Provider = provider,
            PresidioUrl = presidioUrl,
            PresidioFallback = NormalizeFallback(await _settings.GetStringAsync("Security.PiiScanning.PresidioFallback", "BuiltIn")),
            PresidioLowThreshold = await GetDoubleAsync("Security.PiiScanning.PresidioLowThreshold", 0.35, 0.01, 0.98),
            PresidioMediumThreshold = await GetDoubleAsync("Security.PiiScanning.PresidioMediumThreshold", 0.60, 0.01, 0.99),
            PresidioHighThreshold = await GetDoubleAsync("Security.PiiScanning.PresidioHighThreshold", 0.85, 0.01, 1.00),
            PresidioHealthy = health.Healthy,
            PresidioHealthMessage = health.Message,
            EffectiveProvider = effectiveProvider
        });
    }

    [HttpPost("/Admin/Pii/Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(bool enabled, string? sensitivity, string? lowConfidenceAction, string? mediumConfidenceAction,
        bool blockOnFailure, bool blockUnscannable, bool scanContactInfo, string[]? categories,
        int maxExtractedCharacters, int maxPdfPages, int maxArchiveExpandedMB,
        string? provider, string? presidioUrl, string? presidioFallback,
        double presidioLowThreshold, double presidioMediumThreshold, double presidioHighThreshold)
    {
        sensitivity = NormalizeSensitivity(sensitivity);
        lowConfidenceAction = NormalizeAction(lowConfidenceAction, "AllowLog");
        mediumConfidenceAction = NormalizeAction(mediumConfidenceAction, "Warn");
        provider = NormalizeProvider(provider);
        presidioFallback = NormalizeFallback(presidioFallback);
        presidioUrl = NormalizePresidioUrl(presidioUrl);

        var selected = (categories ?? []).Where(c => ValidCategories.Contains(c, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Length == 0)
        {
            TempData["Error"] = "Select at least one PII detection category.";
            return RedirectToAction(nameof(Index));
        }

        maxExtractedCharacters = Math.Clamp(maxExtractedCharacters, 10_000, 10_000_000);
        maxPdfPages = Math.Clamp(maxPdfPages, 1, 5000);
        maxArchiveExpandedMB = Math.Clamp(maxArchiveExpandedMB, 1, 2048);
        presidioLowThreshold = Math.Clamp(presidioLowThreshold, 0.01, 0.98);
        presidioMediumThreshold = Math.Clamp(presidioMediumThreshold, presidioLowThreshold, 0.99);
        presidioHighThreshold = Math.Clamp(presidioHighThreshold, presidioMediumThreshold, 1.00);

        await _settings.UpsertAsync("Security.PiiScanning.Enabled", enabled.ToString(), "Enable server-side PII inspection before transfer submission.");
        await _settings.UpsertAsync("Security.PiiScanning.Mode", "Warn", "Legacy PII mode retained for compatibility; confidence policy controls actions.");
        await _settings.UpsertAsync("Security.PiiScanning.Sensitivity", sensitivity, "PII detection sensitivity profile: Balanced, Strict, or Custom.");
        await _settings.UpsertAsync("Security.PiiScanning.LowConfidenceAction", lowConfidenceAction, "Action for low-confidence PII findings when Custom sensitivity is selected.");
        await _settings.UpsertAsync("Security.PiiScanning.MediumConfidenceAction", mediumConfidenceAction, "Action for medium-confidence PII findings when Custom sensitivity is selected.");
        await _settings.UpsertAsync("Security.PiiScanning.BlockOnFailure", blockOnFailure.ToString(), "Block when PII inspection fails.");
        await _settings.UpsertAsync("Security.PiiScanning.BlockUnscannable", blockUnscannable.ToString(), "Block file types or content that cannot be inspected.");
        await _settings.UpsertAsync("Security.PiiScanning.ScanContactInfo", scanContactInfo.ToString(), "Inspect email addresses and phone numbers as contact information.");
        await _settings.UpsertAsync("Security.PiiScanning.Categories", string.Join(',', selected), "PII categories inspected by FileDrop.");
        await _settings.UpsertAsync("Security.PiiScanning.MaxExtractedCharacters", maxExtractedCharacters.ToString(), "Maximum extracted characters inspected per file.");
        await _settings.UpsertAsync("Security.PiiScanning.MaxPdfPages", maxPdfPages.ToString(), "Maximum PDF pages inspected per file.");
        await _settings.UpsertAsync("Security.PiiScanning.MaxArchiveExpandedMB", maxArchiveExpandedMB.ToString(), "Maximum expanded ZIP content inspected in MB.");
        await _settings.UpsertAsync("Security.PiiScanning.Provider", provider, "PII provider: Auto, Presidio, BuiltIn, or Disabled.");
        await _settings.UpsertAsync("Security.PiiScanning.PresidioUrl", presidioUrl, "Base URL for the local Microsoft Presidio Analyzer service.");
        await _settings.UpsertAsync("Security.PiiScanning.PresidioFallback", presidioFallback, "Action when Presidio is unavailable: BuiltIn, Block, or AllowLog.");
        await _settings.UpsertAsync("Security.PiiScanning.PresidioLowThreshold", presidioLowThreshold.ToString("0.00", CultureInfo.InvariantCulture), "Minimum Presidio score retained as a low-confidence finding.");
        await _settings.UpsertAsync("Security.PiiScanning.PresidioMediumThreshold", presidioMediumThreshold.ToString("0.00", CultureInfo.InvariantCulture), "Presidio score at or above this value is medium confidence.");
        await _settings.UpsertAsync("Security.PiiScanning.PresidioHighThreshold", presidioHighThreshold.ToString("0.00", CultureInfo.InvariantCulture), "Presidio score at or above this value is high confidence.");

        await _audit.WriteAsync(null, User?.Identity?.Name, "PII Protection Updated",
            $"Enabled={enabled}; Provider={provider}; PresidioUrl={presidioUrl}; PresidioFallback={presidioFallback}; Sensitivity={sensitivity}; LowAction={lowConfidenceAction}; MediumAction={mediumConfidenceAction}; HighAction=Block; Categories={string.Join(',', selected)}",
            HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Message"] = "PII protection settings saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Admin/Pii/TestPresidio")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestPresidio(string? presidioUrl, CancellationToken cancellationToken)
    {
        var url = NormalizePresidioUrl(presidioUrl);
        var health = await _presidio.GetHealthAsync(url, cancellationToken);
        TempData[health.Healthy ? "Message" : "Error"] = health.Healthy
            ? $"Presidio connection succeeded: {health.Message}"
            : $"Presidio connection failed: {health.Message}";
        return RedirectToAction(nameof(Index));
    }

    private async Task<double> GetDoubleAsync(string key, double defaultValue, double min, double max)
    {
        var raw = await _settings.GetStringAsync(key, defaultValue.ToString(CultureInfo.InvariantCulture));
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) value = defaultValue;
        return Math.Clamp(value, min, max);
    }

    private static string NormalizeSensitivity(string? value)
        => ValidSensitivity.FirstOrDefault(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? "Balanced";

    private static string NormalizeAction(string? value, string fallback)
        => ValidActions.FirstOrDefault(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? fallback;

    private static string NormalizeProvider(string? value)
        => ValidProviders.FirstOrDefault(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? "Auto";

    private static string NormalizeFallback(string? value)
        => ValidFallbacks.FirstOrDefault(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? "BuiltIn";

    private static string NormalizePresidioUrl(string? value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:5102" : value.Trim();
        return value.TrimEnd('/');
    }
}
