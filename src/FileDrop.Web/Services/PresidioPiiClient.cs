using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileDrop.Web.Services;

public interface IPresidioPiiClient
{
    Task<PresidioHealthResult> GetHealthAsync(string baseUrl, CancellationToken cancellationToken);
    Task<IReadOnlyList<PresidioEntityResult>> AnalyzeAsync(
        string baseUrl,
        string text,
        IReadOnlyCollection<string> entities,
        double scoreThreshold,
        CancellationToken cancellationToken);
}

public sealed class PresidioPiiClient : IPresidioPiiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PresidioPiiClient> _logger;

    public PresidioPiiClient(IHttpClientFactory httpClientFactory, ILogger<PresidioPiiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<PresidioHealthResult> GetHealthAsync(string baseUrl, CancellationToken cancellationToken)
    {
        if (!TryNormalizeBaseUrl(baseUrl, out var normalized, out var error))
            return new PresidioHealthResult(false, error ?? "Invalid Presidio URL.", null);

        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(new Uri(new Uri(normalized), "health"), cancellationToken);
            var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            return response.IsSuccessStatusCode
                ? new PresidioHealthResult(true, string.IsNullOrWhiteSpace(body) ? "Presidio Analyzer is healthy." : body, normalized)
                : new PresidioHealthResult(false, $"Presidio health check returned HTTP {(int)response.StatusCode}.", normalized);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PresidioHealthResult(false, "Presidio health check timed out.", normalized);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Presidio health check failed for {BaseUrl}", normalized);
            return new PresidioHealthResult(false, $"Presidio is unreachable: {ex.Message}", normalized);
        }
    }

    public async Task<IReadOnlyList<PresidioEntityResult>> AnalyzeAsync(
        string baseUrl,
        string text,
        IReadOnlyCollection<string> entities,
        double scoreThreshold,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeBaseUrl(baseUrl, out var normalized, out var error))
            throw new InvalidOperationException(error ?? "Invalid Presidio URL.");

        var payload = new
        {
            text,
            language = "en",
            entities = entities.Count == 0 ? null : entities,
            score_threshold = Math.Clamp(scoreThreshold, 0.01, 1.0),
            return_decision_process = false
        };

        using var client = CreateClient();
        using var response = await client.PostAsJsonAsync(new Uri(new Uri(normalized), "analyze"), payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Presidio Analyzer returned HTTP {(int)response.StatusCode}: {TrimForLog(body, 500)}");

        try
        {
            var results = JsonSerializer.Deserialize<List<PresidioEntityResult>>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return results ?? new List<PresidioEntityResult>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Presidio Analyzer returned an invalid JSON response.", ex);
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("PresidioAnalyzer");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private static bool TryNormalizeBaseUrl(string? value, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Presidio URL is blank.";
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Presidio URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        normalized = uri.ToString().TrimEnd('/') + "/";
        return true;
    }

    private static string TrimForLog(string value, int max)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max] + "...";
    }
}

public sealed record PresidioHealthResult(bool Healthy, string Message, string? NormalizedUrl);

public sealed class PresidioEntityResult
{
    [JsonPropertyName("entity_type")]
    public string EntityType { get; set; } = "";
    [JsonPropertyName("start")]
    public int Start { get; set; }
    [JsonPropertyName("end")]
    public int End { get; set; }
    [JsonPropertyName("score")]
    public double Score { get; set; }
}
