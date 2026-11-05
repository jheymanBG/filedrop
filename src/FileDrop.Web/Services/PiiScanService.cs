using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FileDrop.Web.Models;
using UglyToad.PdfPig;

namespace FileDrop.Web.Services;

public interface IPiiScanService
{
    Task<PiiScanResult> ScanAsync(string path, string originalFileName, CancellationToken cancellationToken);
}

public sealed class PiiScanService : IPiiScanService
{
    private static readonly Regex SsnRegex = new(@"(?<!\d)(?!000|666|9\d{2})(\d{3})([- ]?)(?!00)(\d{2})\2(?!0000)(\d{4})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex CreditCardCandidateRegex = new(@"(?<!\d)(?:\d[ -]?){13,19}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex DobRegex = new(@"\b(?:DOB|Date\s+of\s+Birth|Birth\s*Date)\s*[:#-]?\s*(?:0?[1-9]|1[0-2])[/-](?:0?[1-9]|[12]\d|3[01])[/-](?:19|20)\d{2}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RoutingRegex = new(@"\b(?:routing|ABA)\s*(?:number|no\.?|#)?\s*[:#-]?\s*\d{9}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AccountRegex = new(@"\b(?:account|acct)\s*(?:number|no\.?|#)?\s*[:#-]?\s*\d{6,17}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PassportRegex = new(@"\bpassport\s*(?:number|no\.?|#)?\s*[:#-]?\s*[A-Z0-9]{6,12}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmailRegex = new(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PhoneRegex = new(@"(?<!\d)(?:\+?1[-.\s]?)?(?:\(?\d{3}\)?[-.\s]?)\d{3}[-.\s]?\d{4}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex PageMarkerRegex = new(@"\[\[FILEDROP_PAGE:(\d+)\]\]", RegexOptions.Compiled);

    private static readonly string[] SsnContextTerms =
    {
        "ssn", "social security", "social security number", "taxpayer identification", "tax id"
    };

    private static readonly string[] CardContextTerms =
    {
        "credit card", "card number", "card #", "visa", "mastercard", "master card", "amex",
        "american express", "discover", "cvv", "cvc", "expiration", "expiry"
    };

    private static readonly string[] StrongCardContextTerms =
    {
        "credit card", "card number", "card #", "cvv", "cvc", "expiration", "expiry"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".tsv", ".json", ".xml", ".html", ".htm", ".log", ".md", ".yaml", ".yml", ".ini", ".config", ".sql"
    };

    private readonly ISettingsRepository _settings;
    private readonly IPresidioPiiClient _presidio;
    private readonly ILogger<PiiScanService> _logger;

    public PiiScanService(ISettingsRepository settings, IPresidioPiiClient presidio, ILogger<PiiScanService> logger)
    {
        _settings = settings;
        _presidio = presidio;
        _logger = logger;
    }

    public async Task<PiiScanResult> ScanAsync(string path, string originalFileName, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return Failure("PII scan could not run because the uploaded file is missing.");

        try
        {
            var maxChars = await GetIntAsync("Security.PiiScanning.MaxExtractedCharacters", 2_000_000, 10_000, 10_000_000);
            var maxPdfPages = await GetIntAsync("Security.PiiScanning.MaxPdfPages", 1000, 1, 5000);
            var maxArchiveMb = await GetIntAsync("Security.PiiScanning.MaxArchiveExpandedMB", 100, 1, 2048);
            var contactInfo = await _settings.GetBoolAsync("Security.PiiScanning.ScanContactInfo", false);
            var sensitivity = NormalizeSensitivity(await _settings.GetStringAsync("Security.PiiScanning.Sensitivity", "Balanced"));
            var lowAction = ResolveAction(sensitivity, "Low", await _settings.GetStringAsync("Security.PiiScanning.LowConfidenceAction", "AllowLog"));
            var mediumAction = ResolveAction(sensitivity, "Medium", await _settings.GetStringAsync("Security.PiiScanning.MediumConfidenceAction", "Warn"));
            var enabledCategories = ParseCategories(await _settings.GetStringAsync(
                "Security.PiiScanning.Categories",
                "SSN,CreditCard,DateOfBirth,BankAccount,Passport"));

            var extraction = await ExtractTextAsync(path, originalFileName, maxChars, maxPdfPages, maxArchiveMb, cancellationToken);
            var incompletePdf = Path.GetExtension(originalFileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                && extraction.UnsupportedItems > 0;

            if (extraction.Failed)
            {
                return new PiiScanResult
                {
                    Result = "Failed",
                    Allowed = false,
                    Message = extraction.Message,
                    UnsupportedItems = extraction.UnsupportedItems
                };
            }

            if (string.IsNullOrWhiteSpace(extraction.Text))
            {
                return new PiiScanResult
                {
                    Result = "Unscannable",
                    Allowed = false,
                    Message = "File inspection incomplete; manual GIS review required. " + extraction.Message,
                    UnsupportedItems = extraction.UnsupportedItems
                };
            }

            var providerSetting = NormalizeProvider(await _settings.GetStringAsync("Security.PiiScanning.Provider", "Auto"));
            var presidioUrl = await _settings.GetStringAsync("Security.PiiScanning.PresidioUrl", "http://127.0.0.1:5102");
            var presidioFallback = NormalizeFallback(await _settings.GetStringAsync("Security.PiiScanning.PresidioFallback", "BuiltIn"));
            var lowThreshold = await GetDoubleAsync("Security.PiiScanning.PresidioLowThreshold", 0.35, 0.01, 0.98);
            var mediumThreshold = await GetDoubleAsync("Security.PiiScanning.PresidioMediumThreshold", 0.60, lowThreshold, 0.99);
            var highThreshold = await GetDoubleAsync("Security.PiiScanning.PresidioHighThreshold", 0.85, mediumThreshold, 1.00);

            var providerUsed = "BuiltIn";
            var providerFailed = false;
            List<PiiFindingSummary> findings;

            if (providerSetting.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            {
                return new PiiScanResult
                {
                    Provider = "Disabled",
                    Result = incompletePdf ? "Blocked" : "Disabled",
                    Allowed = !incompletePdf,
                    Message = incompletePdf ? "PDF inspection incomplete; manual review required." : "PII inspection is disabled by administrator policy.",
                    ExtractedCharacters = extraction.Text.Length,
                    UnsupportedItems = extraction.UnsupportedItems
                };
            }

            var shouldTryPresidio = providerSetting.Equals("Auto", StringComparison.OrdinalIgnoreCase) ||
                                    providerSetting.Equals("Presidio", StringComparison.OrdinalIgnoreCase);

            if (shouldTryPresidio)
            {
                try
                {
                    var health = await _presidio.GetHealthAsync(presidioUrl, cancellationToken);
                    if (!health.Healthy)
                        throw new InvalidOperationException(health.Message);

                    var entities = BuildPresidioEntities(enabledCategories, contactInfo);
                    var raw = await _presidio.AnalyzeAsync(presidioUrl, extraction.Text, entities, lowThreshold, cancellationToken);
                    findings = ConvertPresidioFindings(extraction.Text, raw, enabledCategories, contactInfo, lowThreshold, mediumThreshold, highThreshold);
                    AddBuiltInSupplementalFindings(findings, extraction.Text, enabledCategories);
                    providerUsed = "Presidio";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    providerFailed = true;
                    _logger.LogWarning(ex, "Presidio PII provider unavailable for {FileName}; fallback={Fallback}", originalFileName, presidioFallback);

                    if (presidioFallback.Equals("BuiltIn", StringComparison.OrdinalIgnoreCase))
                    {
                        findings = Detect(extraction.Text, enabledCategories, contactInfo, sensitivity);
                        providerUsed = "BuiltInFallback";
                    }
                    else if (presidioFallback.Equals("AllowLog", StringComparison.OrdinalIgnoreCase))
                    {
                        return new PiiScanResult
                        {
                            Provider = "PresidioUnavailable",
                            Result = "ProviderUnavailable",
                            Allowed = false,
                            Message = "PII provider unavailable; file retained for GIS review and not released.",
                            ExtractedCharacters = extraction.Text.Length,
                            UnsupportedItems = extraction.UnsupportedItems
                        };
                    }
                    else
                    {
                        return new PiiScanResult
                        {
                            Provider = "PresidioUnavailable",
                            Result = "Failed",
                            Allowed = false,
                            Message = "Presidio PII inspection was unavailable and administrator policy blocks the upload.",
                            ExtractedCharacters = extraction.Text.Length,
                            UnsupportedItems = extraction.UnsupportedItems
                        };
                    }
                }
            }
            else
            {
                findings = Detect(extraction.Text, enabledCategories, contactInfo, sensitivity);
            }

            var findingCount = findings.Sum(x => x.Count);
            var detected = findingCount > 0;
            var maxConfidence = findings.Select(x => x.Confidence).OrderByDescending(ConfidenceRank).FirstOrDefault() ?? "None";
            var hasHigh = findings.Any(x => x.Confidence.Equals("High", StringComparison.OrdinalIgnoreCase));
            var hasMedium = findings.Any(x => x.Confidence.Equals("Medium", StringComparison.OrdinalIgnoreCase));
            var hasLow = findings.Any(x => x.Confidence.Equals("Low", StringComparison.OrdinalIgnoreCase));
            var partial = extraction.UnsupportedItems > 0;

            var shouldBlock = hasHigh
                || (hasMedium && mediumAction.Equals("Block", StringComparison.OrdinalIgnoreCase))
                || (hasLow && lowAction.Equals("Block", StringComparison.OrdinalIgnoreCase))
                || partial
                || incompletePdf
                || providerFailed;

            var requiresAcknowledgement = !shouldBlock && (
                (hasMedium && mediumAction.Equals("Warn", StringComparison.OrdinalIgnoreCase))
                || (hasLow && lowAction.Equals("Warn", StringComparison.OrdinalIgnoreCase)));

            var logOnly = detected && !shouldBlock && !requiresAcknowledgement;

            var resultName = shouldBlock
                ? "Blocked"
                : requiresAcknowledgement
                    ? "Warning"
                    : logOnly
                        ? "Logged"
                        : partial ? "Partial" : "Clean";

            string message;
            var categoriesText = string.Join(", ", findings.Select(x => x.Category).Distinct(StringComparer.OrdinalIgnoreCase));
            if (shouldBlock)
            {
                message = providerFailed
                    ? "PII provider encountered an error. File securely held for GIS review; no recipient access granted."
                    : partial
                        ? $"File inspection incomplete: {extraction.UnsupportedItems} page(s) or section(s) could not be inspected. Manual review required; the file was not cleared. {extraction.Message}"
                        : $"PII was detected at a confidence level configured to block ({categoriesText}). This file is blocked by policy.";
            }
            else if (requiresAcknowledgement)
            {
                message = $"Potential PII was detected ({categoriesText}). Review the redacted context and acknowledge the warning to continue.";
            }
            else if (logOnly)
            {
                message = $"Low-confidence potential PII was detected ({categoriesText}). The file is allowed and the finding was recorded in the audit history.";
            }
            else if (partial)
            {
                message = $"No actionable PII was detected in inspectable content. {extraction.UnsupportedItems} item(s) could not be inspected.";
            }
            else
            {
                message = "No configured PII patterns were detected.";
            }

            return new PiiScanResult
            {
                Provider = providerUsed,
                Result = resultName,
                Allowed = !shouldBlock && !requiresAcknowledgement,
                RequiresAcknowledgement = requiresAcknowledgement,
                HasHighConfidence = hasHigh,
                MaxConfidence = maxConfidence,
                FindingCount = findingCount,
                Findings = findings,
                Message = message,
                ExtractedCharacters = extraction.Text.Length,
                UnsupportedItems = extraction.UnsupportedItems
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PII scan failed for {FileName}", originalFileName);
            var blockOnFailure = await _settings.GetBoolAsync("Security.PiiScanning.BlockOnFailure", true);
            return new PiiScanResult
            {
                Result = "Failed",
                Allowed = false,
                Message = "PII inspection failed. No sensitive values were written to the application log."
            };
        }
    }

    private static string NormalizeProvider(string? value)
    {
        foreach (var valid in new[] { "Auto", "Presidio", "BuiltIn", "Disabled" })
            if (valid.Equals(value, StringComparison.OrdinalIgnoreCase)) return valid;
        return "Auto";
    }

    private static string NormalizeFallback(string? value)
    {
        foreach (var valid in new[] { "BuiltIn", "Block", "AllowLog" })
            if (valid.Equals(value, StringComparison.OrdinalIgnoreCase)) return valid;
        return "BuiltIn";
    }

    private static IReadOnlyCollection<string> BuildPresidioEntities(HashSet<string> categories, bool contactInfo)
    {
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (categories.Contains("SSN")) entities.Add("US_SSN");
        if (categories.Contains("CreditCard")) entities.Add("CREDIT_CARD");
        if (categories.Contains("BankAccount"))
        {
            entities.Add("US_BANK_NUMBER");
            entities.Add("IBAN_CODE");
        }
        if (categories.Contains("Passport")) entities.Add("US_PASSPORT");
        if (categories.Contains("DriverLicense")) entities.Add("US_DRIVER_LICENSE");
        if (categories.Contains("ITIN")) entities.Add("US_ITIN");
        if (contactInfo)
        {
            entities.Add("EMAIL_ADDRESS");
            entities.Add("PHONE_NUMBER");
        }
        return entities.ToArray();
    }

    private static List<PiiFindingSummary> ConvertPresidioFindings(
        string text,
        IReadOnlyList<PresidioEntityResult> raw,
        HashSet<string> categories,
        bool contactInfo,
        double lowThreshold,
        double mediumThreshold,
        double highThreshold)
    {
        var mapped = new List<(string Category, PresidioEntityResult Item)>();
        foreach (var item in raw)
        {
            var category = item.EntityType.ToUpperInvariant() switch
            {
                "US_SSN" => "SSN",
                "CREDIT_CARD" => "CreditCard",
                "US_BANK_NUMBER" or "IBAN_CODE" => "BankAccount",
                "US_PASSPORT" => "Passport",
                "US_DRIVER_LICENSE" => "DriverLicense",
                "US_ITIN" => "ITIN",
                "EMAIL_ADDRESS" when contactInfo => "EmailAddress",
                "PHONE_NUMBER" when contactInfo => "PhoneNumber",
                _ => ""
            };
            if (string.IsNullOrWhiteSpace(category)) continue;
            if (!category.Equals("EmailAddress", StringComparison.OrdinalIgnoreCase) &&
                !category.Equals("PhoneNumber", StringComparison.OrdinalIgnoreCase) &&
                !categories.Contains(category)) continue;
            if (item.Score < lowThreshold) continue;
            mapped.Add((category, item));
        }

                // Phase73C: individual Presidio findings, no grouping by category.
        var findings = new List<PiiFindingSummary>();

        foreach (var entry in mapped)
        {
            var item = entry.Item;

            var confidence = item.Score >= highThreshold
                ? "High"
                : item.Score >= mediumThreshold
                    ? "Medium"
                    : "Low";

            if (item.Start < 0 ||
                item.End <= item.Start ||
                item.End > text.Length)
            {
                continue;
            }

            var rawValue = text[item.Start..item.End];
            var maskedValue = RedactPresidioValue(entry.Category, rawValue);
            var page = FindPageForOffset(text, item.Start);

            findings.Add(new PiiFindingSummary
            {
                Category = entry.Category,
                Count = 1,
                Severity = confidence,
                Confidence = confidence,
                Location = page.HasValue
                    ? $"Page {page.Value}"
                    : "Page unavailable",
                RedactedValue = maskedValue,
                Context = "",
                Reason = Phase73D1SafeContext(text, item.Start, item.End, item.EntityType, item.Score)
            });
        }

        return findings;
    }

    // Phase73C: map extracted-text offset to its PDF page marker.
    // Phase 73D1:
    // Return a fixed, allowlisted engineering descriptor.
    // Never return raw surrounding text, names, numbers, or identifiers.
    private static string Phase73D1SafeContext(
        string text,
        int start,
        int end,
        string entityType,
        double score)
    {
        var reason =
            $"Microsoft Presidio detected {entityType} with score {score:0.00}.";

        if (string.IsNullOrEmpty(text) ||
            start < 0 ||
            end <= start ||
            end > text.Length)
        {
            return reason;
        }

        // Keep the search window on the same PDF page.
        var windowStart = Math.Max(0, start - 120);
        var windowEnd = Math.Min(text.Length, end + 80);

        const string marker = "[[FILEDROP_PAGE:";

        var lastMarker = text.LastIndexOf(
            marker,
            start,
            StringComparison.Ordinal);

        if (lastMarker >= 0)
        {
            windowStart = Math.Max(windowStart, lastMarker);
        }

        var nextMarker = text.IndexOf(
            marker,
            end,
            StringComparison.Ordinal);

        if (nextMarker >= 0)
        {
            windowEnd = Math.Min(windowEnd, nextMarker);
        }

        if (windowEnd <= windowStart)
        {
            return reason;
        }

        var nearby = text.Substring(
            windowStart,
            windowEnd - windowStart);

        // Each output label is a fixed string.
        // The original document wording is never returned.
        var terms = new (string Pattern, string Label)[]
        {
            (@"\bPUMP\b", "Pump equipment"),
            (@"\bMOTOR\b", "Motor equipment"),
            (@"\bVALVE\b", "Valve equipment"),
            (@"\bEQUIPMENT\b", "Equipment reference"),
            (@"\bMODEL\b", "Model reference"),
            (@"\bSERIAL\b", "Serial number reference"),
            (@"\bPART\b", "Part reference"),
            (@"\bDRAWING\b", "Drawing reference"),
            (@"\bSHEET\b", "Sheet reference"),
            (@"\bSPECIFICATION\b", "Specification reference"),
            (@"\bPANEL\b", "Panel reference"),
            (@"\bELECTRICAL\b", "Electrical equipment"),
            (@"\bMECHANICAL\b", "Mechanical equipment"),
            (@"\bINSTRUMENT\b", "Instrument reference"),
            (@"\bTAG\b", "Equipment tag reference")
        };

        foreach (var term in terms)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(
                nearby,
                term.Pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return reason +
                    " Nearby document terminology: " +
                    term.Label +
                    ". This is contextual information, not a false-positive determination.";
            }
        }

        return reason + " No allowlisted document context identified.";
    }
    private static int? FindPageForOffset(string text, int offset)
    {
        if (offset < 0 || offset >= text.Length)
            return null;

        const string prefix = "[[FILEDROP_PAGE:";

        var marker = text.LastIndexOf(
            prefix,
            offset,
            StringComparison.Ordinal
        );

        if (marker < 0)
            return null;

        var digitsStart = marker + prefix.Length;

        var markerEnd = text.IndexOf(
            "]]",
            digitsStart,
            StringComparison.Ordinal
        );

        if (markerEnd < 0 ||
            markerEnd > offset ||
            markerEnd - digitsStart > 7)
        {
            return null;
        }

        if (int.TryParse(
            text[digitsStart..markerEnd],
            out var page
        ) && page > 0)
        {
            return page;
        }

        return null;
    }
    private static void AddBuiltInSupplementalFindings(List<PiiFindingSummary> findings, string text, HashSet<string> categories)
    {
        // Presidio DATE_TIME is intentionally not used as DOB because it is too broad.
        // Keep FileDrop's explicit-label DOB detector as a narrow supplement.
        if (categories.Contains("DateOfBirth") && !findings.Any(x => x.Category.Equals("DateOfBirth", StringComparison.OrdinalIgnoreCase)))
            AddLabeledFinding(findings, categories, "DateOfBirth", "High", text, DobRegex, "Date-of-birth label and date pattern matched.");
    }

    private static string RedactPresidioValue(string category, string value)
    {
        if (category.Equals("SSN", StringComparison.OrdinalIgnoreCase)) return RedactSsn(value);
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (category.Equals("CreditCard", StringComparison.OrdinalIgnoreCase)) return RedactCard(digits);
        if (digits.Length >= 4) return $"***{digits[^4..]}";
        if (value.Length <= 2) return "[redacted]";
        return value[0] + new string('*', Math.Min(8, value.Length - 2)) + value[^1];
    }

    private sealed record MatchProxy(int Index, int Length, string Value);

    private static string RedactedContext(string text, int index, int length, string redacted)
    {
        var start = Math.Max(0, index - 90);
        var end = Math.Min(text.Length, index + length + 90);
        var before = text[start..index];
        var afterStart = Math.Min(text.Length, index + length);
        var after = text[afterStart..end];
        var context = before + redacted + after;
        context = PageMarkerRegex.Replace(context, " ");
        context = Regex.Replace(context, @"\s+", " ").Trim();
        return context.Length > 260 ? context[..260] + "…" : context;
    }

    private static List<PiiFindingSummary> Detect(string text, HashSet<string> categories, bool contactInfo, string sensitivity)
    {
        var findings = new List<PiiFindingSummary>();
        AddSsnFinding(findings, categories, text, sensitivity);
        AddCreditCardFinding(findings, categories, text, sensitivity);
        AddLabeledFinding(findings, categories, "DateOfBirth", "High", text, DobRegex, "Date-of-birth label and date pattern matched.");
        AddLabeledFinding(findings, categories, "BankAccount", "High", text, new[] { RoutingRegex, AccountRegex }, "Banking label and account/routing pattern matched.");
        AddLabeledFinding(findings, categories, "Passport", "High", text, PassportRegex, "Passport label and identifier pattern matched.");

        if (contactInfo)
        {
            AddLabeledFinding(findings, categories, "EmailAddress", "Medium", text, EmailRegex, "Email-address pattern matched.");
            AddLabeledFinding(findings, categories, "PhoneNumber", "Medium", text, PhoneRegex, "Telephone-number pattern matched.");
        }
        return findings;
    }

    private static void AddSsnFinding(List<PiiFindingSummary> findings, HashSet<string> categories, string text, string sensitivity)
    {
        if (!categories.Contains("SSN")) return;
        var matches = SsnRegex.Matches(text).Cast<Match>().ToList();
        if (matches.Count == 0) return;

        var scored = matches.Select(m => (Match: m, Confidence: SsnConfidence(text, m, sensitivity))).ToList();
        var best = scored.OrderByDescending(x => ConfidenceRank(x.Confidence)).First();
        var confidence = best.Confidence;
        var reason = confidence switch
        {
            "High" => "SSN-format number appeared near explicit Social Security-related terminology.",
            "Medium" => "SSN-like number had supporting Social Security context or strict-mode formatting evidence.",
            _ => "SSN-like numeric pattern matched without enough supporting context for a higher-confidence classification."
        };

        findings.Add(CreateFinding("SSN", matches.Count, confidence, text, best.Match, reason, RedactSsn(best.Match.Value)));
    }

    private static string SsnConfidence(string text, Match match, string sensitivity)
    {
        var window = NearbyText(text, match.Index, match.Length, 160).ToLowerInvariant();
        var hasContext = SsnContextTerms.Any(window.Contains);
        var formatted = match.Value.Contains('-') || match.Value.Contains(' ');

        if (sensitivity.Equals("Strict", StringComparison.OrdinalIgnoreCase))
        {
            if (hasContext) return "High";
            return formatted ? "Medium" : "Low";
        }

        // Balanced/Custom: numeric shape by itself is never more than Low.
        if (hasContext && formatted) return "High";
        if (hasContext) return "Medium";
        return "Low";
    }

    private static void AddCreditCardFinding(List<PiiFindingSummary> findings, HashSet<string> categories, string text, string sensitivity)
    {
        if (!categories.Contains("CreditCard")) return;
        var valid = new List<(Match Match, string Digits, string Confidence, string Reason)>();
        foreach (Match match in CreditCardCandidateRegex.Matches(text))
        {
            var digits = new string(match.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is < 13 or > 19 || !PassesLuhn(digits)) continue;

            var nearby = NearbyText(text, match.Index, match.Length, 180).ToLowerInvariant();
            var hasContext = CardContextTerms.Any(nearby.Contains);
            var strongContext = StrongCardContextTerms.Count(nearby.Contains);
            var recognized = LooksLikePaymentCard(digits);
            var visiblyGrouped = match.Value.Contains(' ') || match.Value.Contains('-');

            string confidence;
            if (sensitivity.Equals("Strict", StringComparison.OrdinalIgnoreCase))
            {
                confidence = hasContext ? "High" : recognized ? "Medium" : "Low";
            }
            else
            {
                // Balanced/Custom: Luhn alone is not enough to interrupt a user.
                if (recognized && strongContext >= 2) confidence = "High";
                else if (hasContext && (recognized || visiblyGrouped)) confidence = "Medium";
                else confidence = "Low";
            }

            var reason = confidence switch
            {
                "High" => "Luhn-valid payment-card-shaped number had multiple nearby payment-card indicators.",
                "Medium" => "Luhn-valid number had supporting payment-card context.",
                _ => recognized
                    ? "Luhn-valid number has a common payment-card prefix/length but lacks sufficient payment context."
                    : "Long numeric sequence passed the Luhn checksum but lacks sufficient payment-card context."
            };
            valid.Add((match, digits, confidence, reason));
        }
        if (valid.Count == 0) return;

        var best = valid.OrderByDescending(x => ConfidenceRank(x.Confidence)).First();
        findings.Add(CreateFinding("CreditCard", valid.Count, best.Confidence, text, best.Match, best.Reason, RedactCard(best.Digits)));
    }

    private static bool LooksLikePaymentCard(string digits)
    {
        if (digits.StartsWith('4') && digits.Length is 13 or 16 or 19) return true;
        if (digits.Length == 16 && int.TryParse(digits[..2], out var p2) && p2 is >= 51 and <= 55) return true;
        if (digits.Length == 16 && int.TryParse(digits[..4], out var p4) && p4 is >= 2221 and <= 2720) return true;
        if (digits.Length == 15 && (digits.StartsWith("34") || digits.StartsWith("37"))) return true;
        if (digits.Length == 16 && (digits.StartsWith("6011") || digits.StartsWith("65"))) return true;
        return false;
    }

    private static string NormalizeSensitivity(string? value)
        => value?.Equals("Strict", StringComparison.OrdinalIgnoreCase) == true ? "Strict"
            : value?.Equals("Custom", StringComparison.OrdinalIgnoreCase) == true ? "Custom"
            : "Balanced";

    private static string ResolveAction(string sensitivity, string confidence, string? configured)
    {
        if (confidence.Equals("High", StringComparison.OrdinalIgnoreCase)) return "Block";

        if (sensitivity.Equals("Balanced", StringComparison.OrdinalIgnoreCase))
            return confidence.Equals("Low", StringComparison.OrdinalIgnoreCase) ? "AllowLog" : "Warn";

        if (sensitivity.Equals("Strict", StringComparison.OrdinalIgnoreCase))
            return "Warn";

        var normalized = configured?.Trim();
        return normalized?.ToLowerInvariant() switch
        {
            "block" => "Block",
            "warn" => "Warn",
            "allowlog" => "AllowLog",
            _ => confidence.Equals("Low", StringComparison.OrdinalIgnoreCase) ? "AllowLog" : "Warn"
        };
    }

    private static void AddLabeledFinding(List<PiiFindingSummary> findings, HashSet<string> categories, string category, string confidence, string text, Regex regex, string reason)
        => AddLabeledFinding(findings, categories, category, confidence, text, new[] { regex }, reason);

    private static void AddLabeledFinding(List<PiiFindingSummary> findings, HashSet<string> categories, string category, string confidence, string text, Regex[] regexes, string reason)
    {
        if (!categories.Contains(category)) return;
        var matches = regexes.SelectMany(r => r.Matches(text).Cast<Match>()).OrderBy(m => m.Index).ToList();
        if (matches.Count == 0) return;
        var first = matches[0];
        findings.Add(CreateFinding(category, matches.Count, confidence, text, first, reason, RedactGeneric(first.Value)));
    }

    private static PiiFindingSummary CreateFinding(string category, int count, string confidence, string text, Match match, string reason, string redacted)
    {
        return new PiiFindingSummary
        {
            Category = category,
            Count = count,
            Severity = confidence,
            Confidence = confidence,
            Location = FindLocation(text, match.Index),
            RedactedValue = redacted,
            Context = RedactedContext(text, match, redacted),
            Reason = reason
        };
    }

    private static string FindLocation(string text, int index)
    {
        var prefix = text[..Math.Clamp(index, 0, text.Length)];
        var marker = PageMarkerRegex.Matches(prefix).Cast<Match>().LastOrDefault();
        return marker is not null && marker.Groups[1].Success ? $"PDF page {marker.Groups[1].Value}" : "Document text";
    }

    private static string RedactedContext(string text, Match match, string redacted)
    {
        var start = Math.Max(0, match.Index - 90);
        var end = Math.Min(text.Length, match.Index + match.Length + 90);
        var before = text[start..match.Index];
        var after = text[(match.Index + match.Length)..end];
        var context = before + redacted + after;
        context = PageMarkerRegex.Replace(context, " ");
        context = Regex.Replace(context, @"\s+", " ").Trim();
        return context.Length > 260 ? context[..260] + "…" : context;
    }

    private static string NearbyText(string text, int index, int length, int radius)
    {
        var start = Math.Max(0, index - radius);
        var end = Math.Min(text.Length, index + length + radius);
        return PageMarkerRegex.Replace(text[start..end], " ");
    }

    private static string RedactSsn(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 ? $"***-**-{digits[^4..]}" : "***-**-****";
    }

    private static string RedactCard(string digits) => digits.Length >= 4 ? $"**** **** **** {digits[^4..]}" : "**** **** **** ****";

    private static string RedactGeneric(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length >= 4) return $"***{digits[^4..]}";
        return "[redacted]";
    }

    private static int ConfidenceRank(string value) => value.ToLowerInvariant() switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0
    };

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var n = digits[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    private static HashSet<string> ParseCategories(string value)
        => value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<ExtractionResult> ExtractTextAsync(string path, string originalFileName, int maxChars, int maxPdfPages, int maxArchiveMb, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(originalFileName);
        if (TextExtensions.Contains(extension))
            return new ExtractionResult(await ReadTextFileAsync(path, maxChars, cancellationToken), "Text content inspected.");

        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
            return await ExtractOpenXmlAsync(path, extension, maxChars, cancellationToken);

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return ExtractPdf(path, maxChars, maxPdfPages);

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return await ExtractZipAsync(path, maxChars, maxArchiveMb, cancellationToken);

        return new ExtractionResult("", $"PII inspection is not supported for {extension ?? "this file type"}.") { UnsupportedItems = 1 };
    }

    private static async Task<string> ReadTextFileAsync(string path, int maxChars, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 65536, leaveOpen: false);
        var buffer = new char[Math.Min(65536, maxChars)];
        var sb = new StringBuilder(Math.Min(maxChars, 262144));
        while (sb.Length < maxChars)
        {
            var requested = Math.Min(buffer.Length, maxChars - sb.Length);
            var read = await reader.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read <= 0) break;
            sb.Append(buffer, 0, read);
        }
        return sb.ToString();
    }

    private static async Task<ExtractionResult> ExtractOpenXmlAsync(string path, string extension, int maxChars, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        var prefixes = extension.ToLowerInvariant() switch
        {
            ".docx" => new[] { "word/document.xml", "word/header", "word/footer", "word/footnotes.xml", "word/endnotes.xml" },
            ".xlsx" => new[] { "xl/sharedStrings.xml", "xl/worksheets/", "xl/comments" },
            ".pptx" => new[] { "ppt/slides/", "ppt/notesSlides/" },
            _ => Array.Empty<string>()
        };
        var sb = new StringBuilder();
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!prefixes.Any(p => entry.FullName.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 65536, leaveOpen: false);
            var xml = await reader.ReadToEndAsync(cancellationToken);
            AppendXmlText(sb, xml, maxChars);
            if (sb.Length >= maxChars) break;
        }
        return new ExtractionResult(sb.ToString(), sb.Length > 0 ? "Office document text inspected." : "No extractable Office document text was found.");
    }

    private ExtractionResult ExtractPdf(string path, int maxChars, int maxPdfPages)
    {
        var sb = new StringBuilder();
        var missingFontPages = 0;
        var noTextPages = 0;
        var extractionLimitPages = 0;
        using var document = PdfDocument.Open(path);

        var totalPages = document.NumberOfPages;
        var pagesToInspect = Math.Min(totalPages, maxPdfPages);
        var pagesBeyondLimit = Math.Max(0, totalPages - maxPdfPages);

        for (var pageNumber = 1; pageNumber <= pagesToInspect; pageNumber++)
        {
            if (sb.Length >= maxChars)
            {
                extractionLimitPages += pagesToInspect - pageNumber + 1;
                break;
            }

            try
            {
                var text = document.GetPage(pageNumber).Text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    // Image-only or otherwise nonextractable pages must never be treated as clean.
                    noTextPages++;
                    continue;
                }

                var marker = $"[[FILEDROP_PAGE:{pageNumber}]]";
                var remaining = maxChars - sb.Length;
                if (remaining <= marker.Length + 1)
                {
                    extractionLimitPages += pagesToInspect - pageNumber + 1;
                    break;
                }

                sb.AppendLine(marker);
                remaining = maxChars - sb.Length;
                var truncated = text.Length > remaining;
                sb.Append(truncated ? text[..remaining] : text);
                if (sb.Length < maxChars) sb.AppendLine();

                if (truncated)
                {
                    extractionLimitPages += pagesToInspect - pageNumber + 1;
                    break;
                }
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("Could not find the font with name", StringComparison.OrdinalIgnoreCase) &&
                ex.Message.Contains("resource store", StringComparison.OrdinalIgnoreCase))
            {
                // Skip only the affected page. Do not log page content or font values.
                missingFontPages++;
                _logger.LogWarning(
                    "PDF page extraction incomplete: missing font resource. Page={Page}; ErrorType={ErrorType}",
                    pageNumber, nameof(InvalidOperationException));
            }
        }

        var unsupportedPages = missingFontPages + noTextPages + extractionLimitPages + pagesBeyondLimit;
        var detail =
            $"MissingFontPages={missingFontPages}; NoExtractableTextPages={noTextPages}; " +
            $"ExtractionLimitPages={extractionLimitPages}; BeyondPageLimit={pagesBeyondLimit}; " +
            $"TotalPages={totalPages}; PageLimit={maxPdfPages}.";

        var message = unsupportedPages > 0
            ? $"PDF text extraction incomplete; {unsupportedPages} page(s) or section(s) were not fully inspected. {detail}"
            : $"PDF text layer inspected. {detail} Image-only pages require OCR and are not inspected in this release.";

        return new ExtractionResult(sb.ToString(), message)
        {
            UnsupportedItems = unsupportedPages
        };
    }

    private static async Task<ExtractionResult> ExtractZipAsync(string path, int maxChars, int maxArchiveMb, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        var maxExpandedBytes = (long)maxArchiveMb * 1024 * 1024;
        long expanded = 0;
        var unsupported = 0;
        var sb = new StringBuilder();
        foreach (var entry in archive.Entries.Take(1000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            expanded += entry.Length;
            if (expanded > maxExpandedBytes)
                return new ExtractionResult(sb.ToString(), "ZIP inspection stopped because the configured expanded-size safety limit was reached.") { UnsupportedItems = unsupported + 1 };
            var ext = Path.GetExtension(entry.Name);
            if (!TextExtensions.Contains(ext)) { unsupported++; continue; }
            sb.AppendLine($"[[FILEDROP_ARCHIVE:{entry.Name}]]");
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 65536, leaveOpen: false);
            var remaining = maxChars - sb.Length;
            if (remaining <= 0) break;
            var buffer = new char[Math.Min(65536, remaining)];
            while (remaining > 0)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read <= 0) break;
                sb.Append(buffer, 0, read);
                remaining -= read;
            }
        }
        return new ExtractionResult(sb.ToString(), unsupported > 0
            ? $"ZIP inspectable text was scanned; {unsupported} unsupported archive item(s) were not inspected."
            : "ZIP text content inspected.") { UnsupportedItems = unsupported };
    }

    private static void AppendXmlText(StringBuilder sb, string xml, int maxChars)
    {
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            foreach (var text in document.DescendantNodes().OfType<XText>())
            {
                if (sb.Length >= maxChars) break;
                var value = text.Value;
                if (string.IsNullOrWhiteSpace(value)) continue;
                var remaining = maxChars - sb.Length;
                sb.Append(value.Length <= remaining ? value : value[..remaining]);
                sb.Append(' ');
            }
        }
        catch { }
    }

    private async Task<double> GetDoubleAsync(string key, double defaultValue, double min, double max)
    {
        var raw = await _settings.GetStringAsync(key, defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            value = defaultValue;
        return Math.Clamp(value, min, max);
    }

    private async Task<int> GetIntAsync(string key, int defaultValue, int min, int max)
    {
        var value = await _settings.GetValueAsync(key);
        return int.TryParse(value, out var parsed) ? Math.Clamp(parsed, min, max) : defaultValue;
    }

    private static PiiScanResult Failure(string message) => new() { Result = "Failed", Allowed = false, Message = message };

    private sealed class ExtractionResult
    {
        public ExtractionResult(string text, string message) { Text = text; Message = message; }
        public string Text { get; }
        public string Message { get; }
        public bool Failed { get; init; }
        public int UnsupportedItems { get; init; }
    }
}
