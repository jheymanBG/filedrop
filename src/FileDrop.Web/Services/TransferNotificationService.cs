using FileDrop.Web.Models;

namespace FileDrop.Web.Services;

public interface ITransferNotificationService
{
    Task NotifyUploadCompleteAsync(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadLink, HttpContext context);
}

public sealed class TransferNotificationService : ITransferNotificationService
{
    private readonly IEmailService _email;
    private readonly IAuditRepository _audit;
    private readonly IConfiguration _config;
    private readonly ILogger<TransferNotificationService> _logger;

    public TransferNotificationService(
        IEmailService email,
        IAuditRepository audit,
        IConfiguration config,
        ILogger<TransferNotificationService> logger)
    {
        _email = email;
        _audit = audit;
        _config = config;
        _logger = logger;
    }

    public async Task NotifyUploadCompleteAsync(TransferRecord transfer, IReadOnlyList<TransferFileRecord> files, string downloadLink, HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress?.ToString();

        var sendRecipient = _config.GetValue<bool>("Notifications:SendRecipientUploadReady", true);
        var sendSender = _config.GetValue<bool?>("Notifications:SendSenderUploadComplete")
            ?? _config.GetValue<bool>("Notifications:SendSenderConfirmation", true);

        if (sendRecipient)
        {
            await TrySendAsync(
                transfer,
                transfer.RecipientEmail,
                "Recipient Upload Ready Email",
                () => _email.SendRecipientUploadReadyAsync(transfer, files, downloadLink),
                remoteIp);
        }
        else
        {
            _logger.LogInformation("Recipient upload-ready notification skipped by configuration. TransferId={TransferId}", transfer.TransferId);
        }

        if (sendSender)
        {
            if (IsUsableSenderEmail(transfer.SenderEmail))
            {
                await TrySendAsync(
                    transfer,
                    transfer.SenderEmail,
                    "Sender Upload Complete Email",
                    () => _email.SendSenderUploadCompleteAsync(transfer, files, downloadLink),
                    remoteIp);
            }
            else
            {
                _logger.LogInformation("Sender upload-complete notification skipped because sender email is not usable. TransferId={TransferId}; SenderEmail={SenderEmail}",
                    transfer.TransferId, transfer.SenderEmail);
            }
        }
        else
        {
            _logger.LogInformation("Sender upload-complete notification skipped by configuration. TransferId={TransferId}", transfer.TransferId);
        }
    }

    private async Task TrySendAsync(TransferRecord transfer, string toEmail, string notificationName, Func<Task> send, string? remoteIp)
    {
        try
        {
            await send();

            await _audit.WriteAsync(
                transfer.TransferId,
                transfer.SenderEmail,
                $"{notificationName} Sent",
                $"To={toEmail}; Recipient={transfer.RecipientEmail}; Subject={transfer.Subject}; Files sent successfully.",
                remoteIp);

            _logger.LogInformation("{NotificationName} sent. TransferId={TransferId}; To={ToEmail}",
                notificationName, transfer.TransferId, toEmail);
        }
        catch (Exception ex)
        {
            await _audit.WriteAsync(
                transfer.TransferId,
                transfer.SenderEmail,
                $"{notificationName} Failed",
                $"To={toEmail}; Error={ex.Message}",
                remoteIp);

            _logger.LogError(ex, "{NotificationName} failed. TransferId={TransferId}; To={ToEmail}",
                notificationName, transfer.TransferId, toEmail);
        }
    }

    private static bool IsUsableSenderEmail(string? senderEmail)
    {
        return !string.IsNullOrWhiteSpace(senderEmail)
            && !senderEmail.Equals("anonymous@local", StringComparison.OrdinalIgnoreCase)
            && !senderEmail.Equals("unknown@bgohio.gov", StringComparison.OrdinalIgnoreCase);
    }
}
