using FileDrop.Web.Services;

namespace FileDrop.Web.Services;

public sealed class RetentionCleanupHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionCleanupHostedService> _logger;

    public RetentionCleanupHostedService(IServiceScopeFactory scopeFactory, ILogger<RetentionCleanupHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Automatic transfer retention cleanup failed.");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task RunIfDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        var cleanup = scope.ServiceProvider.GetRequiredService<ICleanupService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditRepository>();

        var enabled = await settings.GetIntAsync("Retention.AutomaticCleanupEnabled", 0) == 1;
        if (!enabled) return;

        var retentionDays = await settings.GetIntAsync("Retention.Days", 30);
        var cleanupHourUtc = await settings.GetIntAsync("Retention.AutomaticCleanupHourUtc", 3);
        cleanupHourUtc = Math.Clamp(cleanupHourUtc, 0, 23);

        var now = DateTime.UtcNow;
        if (now.Hour != cleanupHourUtc) return;

        var lastRunText = await settings.GetValueAsync("Retention.LastAutomaticCleanupUtc");
        if (DateTime.TryParse(lastRunText, out var lastRun) && lastRun.Date == now.Date) return;

        var deleted = await cleanup.DeleteExpiredTransfersAsync(retentionDays);
        await settings.UpdateAsync("Retention.LastAutomaticCleanupUtc", now.ToString("O"));
        await audit.WriteAsync(null, "system", "Automatic Retention Cleanup", $"RetentionDays={retentionDays}; DeletedTransfers={deleted}", null);

        _logger.LogInformation("Automatic retention cleanup completed. RetentionDays={RetentionDays}; DeletedTransfers={DeletedTransfers}", retentionDays, deleted);
    }
}
