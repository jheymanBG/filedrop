using System.Net;
using System.Net.Mail;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IDownloadNotificationService
{
    Task NotifySenderAsync(Guid transferId, string? fileName, HttpContext context);
}

public sealed class DownloadNotificationService : IDownloadNotificationService
{
    private readonly IConfiguration _config;
    private readonly ISettingsRepository _settings;
    private readonly string _connectionString;
    private readonly ILogger<DownloadNotificationService> _logger;

    public DownloadNotificationService(
        IConfiguration config,
        ISettingsRepository settings,
        ILogger<DownloadNotificationService> logger)
    {
        _config = config;
        _settings = settings;
        _logger = logger;
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task NotifySenderAsync(Guid transferId, string? fileName, HttpContext context)
    {
        var enabled = (await _settings.GetValueAsync("Notifications.SendDownloadConfirmationToSender"))?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        if (!enabled)
        {
            return;
        }

        await using var db = new SqlConnection(_connectionString);
        var transfer = await FindTransferAsync(db, transferId);

        if (transfer is null || string.IsNullOrWhiteSpace(transfer.SenderEmail))
        {
            _logger.LogWarning("Download confirmation skipped. Transfer or sender not found. TransferId={TransferId}", transferId);
            return;
        }

        var alreadySent = await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM dbo.DownloadNotificationLog
            WHERE TransferId = @transferId
              AND SentToEmail = @senderEmail
              AND ISNULL(FileName, '') = ISNULL(@fileName, '')
              AND Status = 'Sent'
            """, new { transferId, senderEmail = transfer.SenderEmail, fileName });

        if (alreadySent > 0)
        {
            return;
        }

        var subject = "FileDrop download confirmation";
        var body = $"""
        <html>
        <body style="font-family:Segoe UI,Arial,sans-serif;">
            <h2>FileDrop download confirmation</h2>
            <p>A recipient downloaded a FileDrop transfer.</p>
            <table>
                <tr><td><strong>Recipient:</strong></td><td>{WebUtility.HtmlEncode(transfer.RecipientEmail ?? "")}</td></tr>
                <tr><td><strong>Subject:</strong></td><td>{WebUtility.HtmlEncode(transfer.Subject ?? "")}</td></tr>
                <tr><td><strong>File:</strong></td><td>{WebUtility.HtmlEncode(fileName ?? "Transfer")}</td></tr>
                <tr><td><strong>Downloaded:</strong></td><td>{DateTime.Now}</td></tr>
                <tr><td><strong>IP Address:</strong></td><td>{WebUtility.HtmlEncode(context.Connection.RemoteIpAddress?.ToString() ?? "")}</td></tr>
            </table>
        </body>
        </html>
        """;

        try
        {
            await SendEmailAsync(transfer.SenderEmail, subject, body);

            await db.ExecuteAsync("""
                INSERT INTO dbo.DownloadNotificationLog
                (TransferId, SentToEmail, RecipientEmail, FileName, Status, Details)
                VALUES
                (@transferId, @senderEmail, @recipientEmail, @fileName, 'Sent', NULL)
                """, new
            {
                transferId,
                senderEmail = transfer.SenderEmail,
                recipientEmail = transfer.RecipientEmail,
                fileName
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download confirmation failed. TransferId={TransferId}", transferId);

            await db.ExecuteAsync("""
                INSERT INTO dbo.DownloadNotificationLog
                (TransferId, SentToEmail, RecipientEmail, FileName, Status, Details)
                VALUES
                (@transferId, @senderEmail, @recipientEmail, @fileName, 'Failed', @details)
                """, new
            {
                transferId,
                senderEmail = transfer.SenderEmail,
                recipientEmail = transfer.RecipientEmail,
                fileName,
                details = ex.ToString()
            });
        }
    }

    private async Task SendEmailAsync(string to, string subject, string htmlBody)
    {
        var server = _config["Email:SmtpServer"] ?? throw new InvalidOperationException("Email:SmtpServer is missing.");
        var port = _config.GetValue<int>("Email:SmtpPort", 25);
        var enableSsl = _config.GetValue<bool>("Email:EnableSsl", false);
        var fromEmail = _config["Email:FromEmail"] ?? "noreply@bgohio.gov";
        var fromName = _config["Email:FromDisplayName"] ?? "FileDrop";

        using var message = new MailMessage
        {
            From = new MailAddress(fromEmail, fromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };

        message.To.Add(to);

        using var client = new SmtpClient(server, port)
        {
            EnableSsl = enableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = true
        };

        await client.SendMailAsync(message);
    }

    private static async Task<TransferNotificationInfo?> FindTransferAsync(SqlConnection db, Guid transferId)
    {
        var tables = (await db.QueryAsync<string>("""
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
            """)).ToList();

        foreach (var table in new[] { "Transfers", "TransferRecords", "Transfer" })
        {
            if (!tables.Contains(table, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var columns = (await db.QueryAsync<string>("""
                SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = @table
                """, new { table })).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!columns.Contains("TransferId"))
            {
                continue;
            }

            var sender = Pick(columns, "SenderEmail", "CreatedByEmail", "FromEmail", "UserEmail");
            var recipient = Pick(columns, "RecipientEmail", "ToEmail");
            var subject = Pick(columns, "Subject", "Title");

            if (sender is null)
            {
                continue;
            }

            var sql = $"""
                SELECT
                    CAST({sender} AS NVARCHAR(255)) AS SenderEmail,
                    {(recipient is null ? "CAST(NULL AS NVARCHAR(255))" : $"CAST({recipient} AS NVARCHAR(255))")} AS RecipientEmail,
                    {(subject is null ? "CAST(NULL AS NVARCHAR(500))" : $"CAST({subject} AS NVARCHAR(500))")} AS Subject
                FROM dbo.{table}
                WHERE TransferId = @transferId
                """;

            var found = await db.QuerySingleOrDefaultAsync<TransferNotificationInfo>(sql, new { transferId });
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? Pick(HashSet<string> columns, params string[] names)
    {
        return names.FirstOrDefault(columns.Contains);
    }

    private sealed class TransferNotificationInfo
    {
        public string? SenderEmail { get; set; }
        public string? RecipientEmail { get; set; }
        public string? Subject { get; set; }
    }
}
