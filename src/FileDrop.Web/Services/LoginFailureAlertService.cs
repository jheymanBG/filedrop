using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace FileDrop.Web.Services;

public interface ILoginFailureAlertService
{
    Task RecordFailureAsync(Exception? failure, HttpContext context);
}

public sealed class LoginFailureAlertService : ILoginFailureAlertService
{
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _config;
    private readonly ISettingsRepository _settings;
    private readonly ILogger<LoginFailureAlertService> _logger;

    public LoginFailureAlertService(IMemoryCache cache, IConfiguration config, ISettingsRepository settings, ILogger<LoginFailureAlertService> logger)
    {
        _cache = cache;
        _config = config;
        _settings = settings;
        _logger = logger;
    }

    public async Task RecordFailureAsync(Exception? failure, HttpContext context)
    {
        var enabled = await _settings.GetBoolAsync("Security.EnableLoginFailureAlerts", _config.GetValue<bool>("Security:EnableLoginFailureAlerts", true));
        if (!enabled) return;

        var threshold = Math.Max(1, await _settings.GetIntAsync("Security.LoginFailureAlertThreshold", _config.GetValue<int>("Security:LoginFailureAlertThreshold", 3)));
        var windowMinutes = Math.Max(1, await _settings.GetIntAsync("Security.LoginFailureWindowMinutes", _config.GetValue<int>("Security:LoginFailureWindowMinutes", 15)));
        var cooldownMinutes = Math.Max(1, await _settings.GetIntAsync("Security.LoginFailureAlertCooldownMinutes", _config.GetValue<int>("Security:LoginFailureAlertCooldownMinutes", 30)));

        var remoteIp = GetRemoteIp(context);
        var key = $"login-fail:{remoteIp}";
        var now = DateTimeOffset.UtcNow;

        var state = _cache.Get<LoginFailureState>(key) ?? new LoginFailureState { FirstFailureUtc = now };
        state.Count++;
        state.LastFailureUtc = now;
        state.LastMessage = failure?.Message ?? "Unknown authentication failure";

        _cache.Set(key, state, now.AddMinutes(windowMinutes));

        if (state.Count < threshold)
        {
            _logger.LogWarning("Login failure recorded. Ip={Ip}; Count={Count}; Threshold={Threshold}; Message={Message}",
                remoteIp, state.Count, threshold, state.LastMessage);
            return;
        }

        if (state.LastAlertUtc is not null && now - state.LastAlertUtc.Value < TimeSpan.FromMinutes(cooldownMinutes))
        {
            _logger.LogWarning("Login failure threshold exceeded, but alert is in cooldown. Ip={Ip}; Count={Count}", remoteIp, state.Count);
            return;
        }

        state.LastAlertUtc = now;
        _cache.Set(key, state, now.AddMinutes(windowMinutes));

        await SendAlertAsync(context, remoteIp, state, failure);
    }

    private async Task SendAlertAsync(HttpContext context, string remoteIp, LoginFailureState state, Exception? failure)
    {
        var configuredTo = await _settings.GetStringAsync("Security.LoginFailureAlertEmail", _config["Security:LoginFailureAlertEmail"] ?? _config["Notifications:SecurityAlertEmail"] ?? _config["Notifications:AlwaysBcc"] ?? "jheyman@bgohio.gov");
        var recipients = SplitEmails(configuredTo).ToArray();
        if (recipients.Length == 0) recipients = ["jheyman@bgohio.gov"];

        try
        {
            using var message = CreateMessage();
            foreach (var recipient in recipients) message.To.Add(recipient);
            message.Subject = "FileDrop repeated failed login attempts";

            var request = context.Request;
            var userAgent = request.Headers.UserAgent.ToString();
            var forwardedFor = request.Headers["X-Forwarded-For"].ToString();
            var referer = request.Headers.Referer.ToString();
            var url = $"{request.Scheme}://{request.Host}{request.Path}{request.QueryString}";

            var text = $"""
            FileDrop detected repeated failed login attempts.

            Failed attempts: {state.Count}
            First failure UTC: {state.FirstFailureUtc:u}
            Last failure UTC: {state.LastFailureUtc:u}
            Remote IP: {remoteIp}
            X-Forwarded-For: {forwardedFor}
            Request URL: {url}
            Referer: {referer}
            User Agent: {userAgent}

            Failure:
            {failure?.Message ?? state.LastMessage ?? "Unknown authentication failure"}
            """;

            var html = $"""
            <!doctype html>
            <html>
            <body style="font-family:Segoe UI,Arial,sans-serif;background:#f5f7f4;padding:24px;color:#222;">
              <div style="max-width:720px;margin:auto;background:#fff;border:1px solid #d9e1d7;border-radius:12px;overflow:hidden;">
                <div style="background:#0f5f3f;color:#fff;padding:20px 24px;">
                  <h2 style="margin:0;">FileDrop Security Alert</h2>
                  <div>Repeated failed login attempts detected</div>
                </div>
                <div style="padding:24px;">
                  <p><strong>Failed attempts:</strong> {state.Count}</p>
                  <p><strong>Remote IP:</strong> {WebUtility.HtmlEncode(remoteIp)}</p>
                  <p><strong>X-Forwarded-For:</strong> {WebUtility.HtmlEncode(forwardedFor)}</p>
                  <p><strong>First failure UTC:</strong> {state.FirstFailureUtc:u}</p>
                  <p><strong>Last failure UTC:</strong> {state.LastFailureUtc:u}</p>
                  <p><strong>Request URL:</strong><br>{WebUtility.HtmlEncode(url)}</p>
                  <p><strong>Referer:</strong><br>{WebUtility.HtmlEncode(referer)}</p>
                  <p><strong>User Agent:</strong><br>{WebUtility.HtmlEncode(userAgent)}</p>
                  <p><strong>Failure:</strong><br>{WebUtility.HtmlEncode(failure?.Message ?? state.LastMessage ?? "Unknown authentication failure")}</p>
                </div>
              </div>
            </body>
            </html>
            """;

            AddBodies(message, text, html);
            await SendAsync(message);
            _logger.LogWarning("Repeated failed login alert sent. To={To}; Ip={Ip}; Count={Count}", configuredTo, remoteIp, state.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not send repeated failed login alert. Ip={Ip}; Count={Count}", remoteIp, state.Count);
        }
    }

    private MailMessage CreateMessage()
    {
        var fromEmail = _config["Email:FromEmail"] ?? "noreply@bgohio.gov";
        var fromName = _config["Email:FromDisplayName"] ?? "GIS Department";
        return new MailMessage { From = new MailAddress(fromEmail, fromName, Encoding.UTF8) };
    }

    private static void AddBodies(MailMessage message, string text, string html)
    {
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(text, Encoding.UTF8, MediaTypeNames.Text.Plain));
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html));
    }

    private async Task SendAsync(MailMessage message)
    {
        var server = _config["Email:SmtpServer"] ?? throw new InvalidOperationException("Email:SmtpServer is missing.");
        var port = _config.GetValue<int>("Email:SmtpPort", 25);
        var enableSsl = _config.GetValue<bool>("Email:EnableSsl", false);

        using var client = new SmtpClient(server, port)
        {
            EnableSsl = enableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = CredentialCache.DefaultNetworkCredentials
        };

        await client.SendMailAsync(message);
    }

    private static IEnumerable<string> SplitEmails(string value)
    {
        return value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(v => v.Contains('@'));
    }

    private static string GetRemoteIp(HttpContext context)
    {
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            return forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "unknown";
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private sealed class LoginFailureState
    {
        public int Count { get; set; }
        public DateTimeOffset FirstFailureUtc { get; set; }
        public DateTimeOffset LastFailureUtc { get; set; }
        public DateTimeOffset? LastAlertUtc { get; set; }
        public string? LastMessage { get; set; }
    }
}
