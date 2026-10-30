using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

// Phase 73E: email notifications only; no access to upload storage
// and no permission to release or change review decisions.
public sealed class PiiReviewNotificationService
{
    private readonly IConfiguration _config;
    private readonly ILogger<PiiReviewNotificationService> _logger;

    public PiiReviewNotificationService(
        IConfiguration config,
        ILogger<PiiReviewNotificationService> logger)
    {
        _config = config;
        _logger = logger;
    }

    private bool Enabled =>
        _config.GetValue<bool>("PiiReviewNotifications:Enabled", false);

    public async Task NotifyRequestedAsync(
        long reviewId, Guid uploadId, string requester)
    {
        if (!Enabled) return;

        var inboxes = await ConfiguredAdministratorEmailsAsync();
        if (inboxes.Count == 0)
        {
            _logger.LogWarning("PII notification skipped: no active administrators. ReviewId={ReviewId}", reviewId);
            return;
        }

        var url = ReviewUrl("/Admin/Pii/Reviews");

        var subject = $"[FileDrop] PII review pending - #{reviewId}";
        var body =
            "A PII review request has been submitted.\r\n\r\n" +
            $"Review ID: {reviewId}\r\n" +
            $"Requester: {requester}\r\n" +
            $"Upload ID: {uploadId}\r\n" +
            $"Administrator review: {url}\r\n\r\n" +
            "The file remains blocked. This email contains no detected PII values.";

        foreach (var inbox in inboxes)
            await SendSafelyAsync(inbox, subject, body, reviewId, "Requested");
    }

    public async Task NotifyDecidedAsync(long reviewId, string decision)
    {
        if (!Enabled) return;

        string? requester;
        try
        {
            var connectionString = _config.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("DefaultConnection missing.");

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = new SqlCommand(
                "SELECT RequestedBy FROM dbo.PiiReviewRequests " +
                "WHERE ReviewId = @reviewId AND Status <> 'Pending' AND ReviewedUtc IS NOT NULL",
                connection);

            command.Parameters.AddWithValue("@reviewId", reviewId);
            requester = (await command.ExecuteScalarAsync()) as string;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unable to resolve PII review requester. ReviewId={ReviewId}",
                reviewId);
            return;
        }

        if (string.IsNullOrWhiteSpace(requester))
        {
            _logger.LogWarning(
                "PII decision notification skipped: requester not found. ReviewId={ReviewId}",
                reviewId);
            return;
        }

        var url = ReviewUrl("/Admin/Pii/Reviews");
        var subject = $"[FileDrop] PII review decision - #{reviewId}";
        var body =
            $"PII review #{reviewId} has been {decision.ToLowerInvariant()}.\r\n\r\n" +
            $"Decision: {decision}\r\n" +
            (decision == "Approved" ? "The file has been released as a secure transfer.\r\n" : "The upload remains blocked.\r\n") +
            $"Review details: {url}";

        var destinations = await ConfiguredAdministratorEmailsAsync();
        destinations.Add(requester);
        foreach (var address in destinations.Distinct(StringComparer.OrdinalIgnoreCase))
            await SendSafelyAsync(address, subject, body, reviewId, "Decided");
    }

    private async Task<HashSet<string>> ConfiguredAdministratorEmailsAsync()
    {
        var configured = _config["PiiReviewNotifications:AdminEmail"];
        if (string.IsNullOrWhiteSpace(configured))
            return await ActiveAdministratorEmailsAsync();
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in configured.Split(
            new[] { ';', ',' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            addresses.Add(new MailAddress(item).Address);
        }
        if (addresses.Count == 0)
            throw new InvalidOperationException("No PII administrator email configured.");
        return addresses;
    }

    private async Task<HashSet<string>> ActiveAdministratorEmailsAsync()
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cs = _config.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("DefaultConnection missing.");
            await using var connection = new SqlConnection(cs);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT Email FROM dbo.FileDropAdminUsers WHERE IsActive = 1", connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var address = reader.GetString(0).Trim();
                try { addresses.Add(new MailAddress(address).Address); }
                catch (FormatException) { _logger.LogWarning("Invalid administrator email skipped."); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to load active administrators for PII notifications.");
        }
        return addresses;
    }

    private string ReviewUrl(string path)
    {
        var configured = _config["PiiReviewNotifications:BaseUrl"];
        if (string.IsNullOrWhiteSpace(configured) ||
            !Uri.TryCreate(configured, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "PiiReviewNotifications:BaseUrl must be a configured HTTPS URL.");
        }

        return configured.TrimEnd('/') + path;
    }

    private async Task SendSafelyAsync(
        string destination, string subject, string body,
        long reviewId, string eventType)
    {
        try
        {
            var server = _config["Email:SmtpServer"]
                ?? throw new InvalidOperationException("SMTP server missing.");

            var from = _config["Email:FromEmail"] ?? "noreply@bgohio.gov";
            var fromName = _config["Email:FromDisplayName"] ?? "GIS Department";
            var port = _config.GetValue<int>("Email:SmtpPort", 25);
            var ssl = _config.GetValue<bool>("Email:EnableSsl", false);

            using var message = new MailMessage
            {
                From = new MailAddress(from, fromName, Encoding.UTF8),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };
            message.To.Add(destination);

            using var client = new SmtpClient(server, port)
            {
                EnableSsl = ssl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = CredentialCache.DefaultNetworkCredentials
            };

            await client.SendMailAsync(message);

            _logger.LogInformation(
                "PII review notification sent. ReviewId={ReviewId}; Type={Type}",
                reviewId, eventType);
        }
        catch (Exception ex)
        {
            // Never undo an already-committed review request or decision.
            _logger.LogError(ex,
                "PII review email delivery failed. ReviewId={ReviewId}; Type={Type}",
                reviewId, eventType);
        }
    }
}