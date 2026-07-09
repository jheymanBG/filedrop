using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;

namespace FileDrop.Web.Services;

public sealed class LowDiskSpaceMonitorHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<LowDiskSpaceMonitorHostedService> _logger;
    private DateTimeOffset? _lastAlertUtc;

    public LowDiskSpaceMonitorHostedService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<LowDiskSpaceMonitorHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = 30;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
                interval = Math.Max(5, await settings.GetIntAsync("Storage.LowDiskSpaceCheckIntervalMinutes", 30));
                await CheckDiskSpaceAsync(settings, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Low disk space monitor failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(interval), stoppingToken);
        }
    }

    private async Task CheckDiskSpaceAsync(ISettingsRepository settings, CancellationToken cancellationToken)
    {
        var enabled = await settings.GetBoolAsync("Storage.EnableLowDiskSpaceAlerts", true);
        if (!enabled) return;

        var root = _config["Storage:RootPath"] ?? _config["Storage:FilesPath"] ?? "C:\\SecureFileTransfer";
        var rootPath = Path.GetPathRoot(Path.GetFullPath(root));
        if (string.IsNullOrWhiteSpace(rootPath)) return;

        var drive = new DriveInfo(rootPath);
        if (!drive.IsReady)
        {
            _logger.LogWarning("Storage drive is not ready. Root={Root}; Drive={Drive}", root, rootPath);
            return;
        }

        var totalBytes = drive.TotalSize;
        var freeBytes = drive.AvailableFreeSpace;
        var freePercent = totalBytes <= 0 ? 0 : (double)freeBytes / totalBytes * 100.0;
        var freeGb = freeBytes / 1024d / 1024d / 1024d;

        var thresholdPercent = Math.Clamp(await settings.GetIntAsync("Storage.LowDiskSpaceThresholdPercentFree", 10), 1, 99);
        var thresholdGb = Math.Max(1, await settings.GetIntAsync("Storage.LowDiskSpaceThresholdFreeGb", 25));
        var cooldownMinutes = Math.Max(5, await settings.GetIntAsync("Storage.LowDiskSpaceAlertCooldownMinutes", 120));

        var belowPercent = freePercent <= thresholdPercent;
        var belowGb = freeGb <= thresholdGb;
        if (!belowPercent && !belowGb) return;

        var now = DateTimeOffset.UtcNow;
        if (_lastAlertUtc is not null && now - _lastAlertUtc.Value < TimeSpan.FromMinutes(cooldownMinutes))
        {
            _logger.LogWarning("Low disk space detected but alert is in cooldown. FreeGB={FreeGB:0.##}; FreePercent={FreePercent:0.##}", freeGb, freePercent);
            return;
        }

        _lastAlertUtc = now;
        await SendAlertAsync(settings, root, drive, freeGb, freePercent, thresholdGb, thresholdPercent, cancellationToken);
    }

    private async Task SendAlertAsync(ISettingsRepository settings, string storageRoot, DriveInfo drive, double freeGb, double freePercent, int thresholdGb, int thresholdPercent, CancellationToken cancellationToken)
    {
        var configuredTo = await settings.GetStringAsync("Storage.LowDiskSpaceAlertEmails", _config["Notifications:StorageAlertEmail"] ?? _config["Notifications:AlwaysBcc"] ?? "jheyman@bgohio.gov");
        var recipients = SplitEmails(configuredTo).ToArray();
        if (recipients.Length == 0) recipients = ["jheyman@bgohio.gov"];

        try
        {
            using var message = CreateMessage();
            foreach (var recipient in recipients) message.To.Add(recipient);
            message.Subject = "FileDrop low disk space warning";

            var totalGb = drive.TotalSize / 1024d / 1024d / 1024d;
            var usedGb = totalGb - freeGb;

            var text = $"""
            FileDrop detected low disk space.

            Storage root: {storageRoot}
            Drive: {drive.Name}
            Total: {totalGb:0.##} GB
            Used: {usedGb:0.##} GB
            Free: {freeGb:0.##} GB ({freePercent:0.##}%)

            Alert thresholds:
            Free percent <= {thresholdPercent}%
            Free space <= {thresholdGb} GB
            """;

            var html = $"""
            <!doctype html>
            <html>
            <body style="font-family:Segoe UI,Arial,sans-serif;background:#f5f7f4;padding:24px;color:#222;">
              <div style="max-width:720px;margin:auto;background:#fff;border:1px solid #d9e1d7;border-radius:12px;overflow:hidden;">
                <div style="background:#8a4b00;color:#fff;padding:20px 24px;">
                  <h2 style="margin:0;">FileDrop Storage Warning</h2>
                  <div>Low disk space detected</div>
                </div>
                <div style="padding:24px;">
                  <p><strong>Storage root:</strong> {WebUtility.HtmlEncode(storageRoot)}</p>
                  <p><strong>Drive:</strong> {WebUtility.HtmlEncode(drive.Name)}</p>
                  <p><strong>Total:</strong> {totalGb:0.##} GB</p>
                  <p><strong>Used:</strong> {usedGb:0.##} GB</p>
                  <p><strong>Free:</strong> {freeGb:0.##} GB ({freePercent:0.##}%)</p>
                  <p><strong>Thresholds:</strong> Free percent <= {thresholdPercent}% or free space <= {thresholdGb} GB</p>
                </div>
              </div>
            </body>
            </html>
            """;

            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(text, Encoding.UTF8, MediaTypeNames.Text.Plain));
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html));

            await SendAsync(message, cancellationToken);
            _logger.LogWarning("Low disk space alert sent. To={To}; Drive={Drive}; FreeGB={FreeGB:0.##}; FreePercent={FreePercent:0.##}", configuredTo, drive.Name, freeGb, freePercent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not send low disk space alert.");
        }
    }

    private MailMessage CreateMessage()
    {
        var fromEmail = _config["Email:FromEmail"] ?? "noreply@bgohio.gov";
        var fromName = _config["Email:FromDisplayName"] ?? "GIS Department";
        return new MailMessage { From = new MailAddress(fromEmail, fromName, Encoding.UTF8) };
    }

    private async Task SendAsync(MailMessage message, CancellationToken cancellationToken)
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

        await client.SendMailAsync(message, cancellationToken);
    }

    private static IEnumerable<string> SplitEmails(string value)
    {
        return value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(v => v.Contains('@'));
    }
}
