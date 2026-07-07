namespace FileDrop.Web.Services;

public sealed class ChunkedUploadCleanupHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChunkedUploadCleanupHostedService> _logger;
    private readonly IConfiguration _config;

    public ChunkedUploadCleanupHostedService(IServiceScopeFactory scopeFactory, ILogger<ChunkedUploadCleanupHostedService> logger, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = Math.Max(15, _config.GetValue<int?>("Upload:CleanupIntervalMinutes") ?? 60);
        var abandonHours = Math.Max(1, _config.GetValue<int?>("Upload:AbandonAfterHours") ?? 24);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
                using var scope = _scopeFactory.CreateScope();
                var uploads = scope.ServiceProvider.GetRequiredService<IChunkedUploadService>();
                var count = await uploads.CleanupAbandonedAsync(TimeSpan.FromHours(abandonHours), stoppingToken);
                if (count > 0) _logger.LogInformation("Marked {Count} abandoned chunked upload session(s).", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chunked upload cleanup failed.");
            }
        }
    }
}
