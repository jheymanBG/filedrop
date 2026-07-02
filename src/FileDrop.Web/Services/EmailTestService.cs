using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;

namespace FileDrop.Web.Services;

public interface IEmailTestService
{
    Task SendTestAsync(string toEmail);
}

public sealed class EmailTestService : IEmailTestService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailTestService> _logger;

    public EmailTestService(IConfiguration config, ILogger<EmailTestService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendTestAsync(string toEmail)
    {
        var smtpServer = _config["Email:SmtpServer"];
        var fromEmail = _config["Email:FromEmail"] ?? "noreply@bgohio.gov";
        var fromName = _config["Email:FromDisplayName"] ?? "GIS Department";
        var port = _config.GetValue<int>("Email:SmtpPort", 25);
        var enableSsl = _config.GetValue<bool>("Email:EnableSsl", false);

        if (string.IsNullOrWhiteSpace(smtpServer))
        {
            throw new InvalidOperationException("Email:SmtpServer is missing.");
        }

        using var message = new MailMessage
        {
            From = new MailAddress(fromEmail, fromName, Encoding.UTF8),
            Subject = "FileDrop SMTP Test",
            Body = "This is a FileDrop SMTP test message.",
            IsBodyHtml = false
        };

        message.To.Add(toEmail);

        var bcc = _config["Notifications:AlwaysBcc"];
        if (!string.IsNullOrWhiteSpace(bcc))
        {
            message.Bcc.Add(bcc);
        }

        var html = $"""
        <html>
        <body style="font-family:Segoe UI,Arial,sans-serif;">
          <h2>FileDrop SMTP Test</h2>
          <p>This is a test message from FileDrop.</p>
          <p><strong>Server:</strong> {WebUtility.HtmlEncode(smtpServer)}</p>
          <p><strong>From:</strong> {WebUtility.HtmlEncode(fromName)} &lt;{WebUtility.HtmlEncode(fromEmail)}&gt;</p>
          <p><strong>Sent:</strong> {DateTime.Now}</p>
        </body>
        </html>
        """;

        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Body, Encoding.UTF8, MediaTypeNames.Text.Plain));
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html));

        using var client = new SmtpClient(smtpServer, port)
        {
            EnableSsl = enableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = CredentialCache.DefaultNetworkCredentials
        };

        await client.SendMailAsync(message);

        _logger.LogInformation("SMTP test sent to {ToEmail} via {Server}:{Port}", toEmail, smtpServer, port);
    }
}
