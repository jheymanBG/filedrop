using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using FileDrop.Web.Models;

namespace FileDrop.Web.Services;

public interface IEmailService
{
    Task SendTransferCreatedAsync(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadLink);
    Task SendRecipientUploadReadyAsync(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadLink);
    Task SendSenderUploadCompleteAsync(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadLink);
    Task SendDownloadNotificationAsync(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadedItem, string? ipAddress);
}

public sealed class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendTransferCreatedAsync(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadLink)
    {
        var publicBaseUrl = _config["Email:PublicBaseUrl"]?.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            downloadLink = $"{publicBaseUrl}/Transfer/Download/{transfer.DownloadToken}";
        }

        try
        {
            await SendRecipientUploadReadyAsync(transfer, files, downloadLink);
            await SendSenderUploadCompleteAsync(transfer, files, downloadLink);

            _logger.LogInformation("Transfer email sent. To={Recipient}; BCC={Bcc}; TransferId={TransferId}; Files={FileCount}",
                transfer.RecipientEmail, _config["Notifications:AlwaysBcc"], transfer.TransferId, files.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email failed for TransferId={TransferId}", transfer.TransferId);
        }
    }

    public async Task SendDownloadNotificationAsync(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadedItem, string? ipAddress)
    {
        var enabled = _config.GetValue<bool>("Notifications:SendDownloadNotification", true);
        if (!enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(transfer.SenderEmail) ||
            transfer.SenderEmail.Equals("anonymous@local", StringComparison.OrdinalIgnoreCase) ||
            transfer.SenderEmail.Equals("unknown@bgohio.gov", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Download notification skipped because sender email is not usable. TransferId={TransferId}", transfer.TransferId);
            return;
        }

        try
        {
            using var message = CreateMessage();
            message.To.Add(transfer.SenderEmail);

            var bcc = _config["Notifications:DownloadNotifyAlwaysBcc"] ?? _config["Notifications:AlwaysBcc"];
            if (!string.IsNullOrWhiteSpace(bcc))
            {
                message.Bcc.Add(bcc);
            }

            message.Subject = "FileDrop download notification";

            var html = BuildDownloadNotificationHtml(transfer, files, downloadedItem, ipAddress);
            var text = BuildDownloadNotificationText(transfer, files, downloadedItem, ipAddress);
            AddBodies(message, text, html);

            await SendAsync(message);

            _logger.LogInformation("Download notification sent. To={Sender}; TransferId={TransferId}; Item={Item}",
                transfer.SenderEmail, transfer.TransferId, downloadedItem);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download notification failed. TransferId={TransferId}", transfer.TransferId);
        }
    }

    public async Task SendRecipientUploadReadyAsync(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadLink)
    {
        var subject = string.IsNullOrWhiteSpace(transfer.Subject)
            ? "City of Bowling Green Secure File Transfer"
            : $"City of Bowling Green Secure File Transfer - {transfer.Subject}";

        using var message = CreateMessage();
        message.To.Add(transfer.RecipientEmail);
        AddBcc(message);
        message.Subject = subject;
        AddBodies(message, BuildRecipientText(transfer, files, downloadLink), BuildRecipientHtml(transfer, files, downloadLink));
        await SendAsync(message);
    }

    public async Task SendSenderUploadCompleteAsync(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadLink)
    {
        var enabled = _config.GetValue<bool?>("Notifications:SendSenderUploadComplete")
            ?? _config.GetValue<bool>("Notifications:SendSenderConfirmation", true);

        if (!enabled)
        {
            return;
        }

        var senderEmail = transfer.SenderEmail;
        if (string.IsNullOrWhiteSpace(senderEmail) || senderEmail.Equals("anonymous@local", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var message = CreateMessage();
        message.To.Add(senderEmail);
        message.Subject = "FileDrop upload complete";
        AddBodies(message, BuildSenderText(transfer, files, downloadLink), BuildSenderHtml(transfer, files, downloadLink));
        await SendAsync(message);
    }

    private MailMessage CreateMessage()
    {
        var fromEmail = _config["Email:FromEmail"] ?? "noreply@bgohio.gov";
        var fromName = _config["Email:FromDisplayName"] ?? "GIS Department";
        return new MailMessage { From = new MailAddress(fromEmail, fromName, Encoding.UTF8) };
    }

    private void AddBcc(MailMessage message)
    {
        var bcc = _config["Notifications:AlwaysBcc"];
        if (!string.IsNullOrWhiteSpace(bcc))
        {
            message.Bcc.Add(bcc);
        }
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

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string FileListHtml(IReadOnlyList<TransferFileRecord> files)
    {
        var sb = new StringBuilder("<ul>");
        foreach (var file in files)
        {
            var mb = Math.Round(file.FileSizeBytes / 1024.0 / 1024.0, 2);
            sb.Append("<li>").Append(E(file.OriginalFileName)).Append(" - ").Append(mb).Append(" MB</li>");
        }
        sb.Append("</ul>");
        return sb.ToString();
    }

    private static string FileListText(IReadOnlyList<TransferFileRecord> files)
    {
        var sb = new StringBuilder();
        foreach (var file in files)
        {
            var mb = Math.Round(file.FileSizeBytes / 1024.0 / 1024.0, 2);
            sb.AppendLine($"- {file.OriginalFileName} ({mb} MB)");
        }
        return sb.ToString();
    }

    private static string BuildRecipientHtml(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadLink)
    {
        var message = string.IsNullOrWhiteSpace(transfer.Message) ? "" : $"<p>{E(transfer.Message).Replace("\n", "<br>")}</p>";
        return $"""
        <!doctype html>
        <html>
        <body style="font-family:Segoe UI,Arial,sans-serif;background:#f5f7f4;padding:24px;color:#222;">
          <div style="max-width:680px;margin:auto;background:#fff;border:1px solid #d9e1d7;border-radius:12px;overflow:hidden;">
            <div style="background:#0f5f3f;color:#fff;padding:22px 28px;">
              <h2 style="margin:0;">City of Bowling Green</h2>
              <div style="opacity:.9;">Secure File Transfer</div>
            </div>
            <div style="padding:28px;">
              <p><strong>The GIS Department has sent you secure files.</strong></p>
              {message}
              <p><strong>Files:</strong></p>
              {FileListHtml(files)}
              <p><a href="{E(downloadLink)}" style="display:inline-block;background:#0f5f3f;color:#fff;padding:12px 18px;border-radius:6px;text-decoration:none;font-weight:700;">Download Files</a></p>
              <p style="font-size:14px;color:#555;">This secure link expires on {transfer.ExpirationDate.ToLocalTime():MMMM d, yyyy h:mm tt}.</p>
              <p style="font-size:13px;color:#777;">If the button does not work, copy and paste this link:<br>{E(downloadLink)}</p>
            </div>
          </div>
        </body>
        </html>
        """;
    }

    private static string BuildRecipientText(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadLink)
    {
        return $"""
        City of Bowling Green Secure File Transfer

        The GIS Department has sent you secure files.

        {transfer.Message}

        Files:
        {FileListText(files)}

        Download:
        {downloadLink}

        This secure link expires on {transfer.ExpirationDate.ToLocalTime():MMMM d, yyyy h:mm tt}.
        """;
    }

    private static string BuildSenderHtml(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadLink)
    {
        return $"""
        <html><body style="font-family:Segoe UI,Arial,sans-serif;">
          <h2>FileDrop Upload Complete</h2>
          <p>Your FileDrop upload completed successfully and the recipient notification was generated.</p>
          <p><strong>Recipient:</strong> {E(transfer.RecipientEmail)}</p>
          <p><strong>Expires:</strong> {transfer.ExpirationDate.ToLocalTime():MMMM d, yyyy h:mm tt}</p>
          <p><strong>Files:</strong></p>
          {FileListHtml(files)}
          <p><strong>Download Link:</strong><br><a href="{E(downloadLink)}">{E(downloadLink)}</a></p>
        </body></html>
        """;
    }

    private static string BuildSenderText(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadLink)
    {
        return $"""
        FileDrop Upload Complete

        Your FileDrop upload completed successfully and the recipient notification was generated.

        Recipient: {transfer.RecipientEmail}
        Expires: {transfer.ExpirationDate.ToLocalTime():MMMM d, yyyy h:mm tt}

        Files:
        {FileListText(files)}

        Download Link:
        {downloadLink}
        """;
    }

    private static string BuildDownloadNotificationHtml(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadedItem, string? ipAddress)
    {
        return $"""
        <html>
        <body style="font-family:Segoe UI,Arial,sans-serif;">
          <h2>FileDrop Download Notification</h2>
          <p>A FileDrop item was downloaded.</p>
          <p><strong>Recipient:</strong> {E(transfer.RecipientEmail)}</p>
          <p><strong>Subject:</strong> {E(transfer.Subject)}</p>
          <p><strong>Downloaded:</strong> {E(downloadedItem)}</p>
          <p><strong>Download Time:</strong> {DateTime.Now:MMMM d, yyyy h:mm tt}</p>
          <p><strong>IP Address:</strong> {E(ipAddress)}</p>
          <p><strong>Transfer ID:</strong> {transfer.TransferId}</p>
          <p><strong>Files in transfer:</strong></p>
          {FileListHtml(files)}
        </body>
        </html>
        """;
    }

    private static string BuildDownloadNotificationText(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadedItem, string? ipAddress)
    {
        return $"""
        FileDrop Download Notification

        Recipient: {transfer.RecipientEmail}
        Subject: {transfer.Subject}
        Downloaded: {downloadedItem}
        Download Time: {DateTime.Now:MMMM d, yyyy h:mm tt}
        IP Address: {ipAddress}
        Transfer ID: {transfer.TransferId}

        Files in transfer:
        {FileListText(files)}
        """;
    }
}
