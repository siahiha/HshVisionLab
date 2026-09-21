namespace HshDetectionService;

public sealed class ServiceWorker : BackgroundService
{
    private readonly DetectionRuntimeHost _runtime;
    private readonly ILogger<ServiceWorker> _logger;

    public ServiceWorker(DetectionRuntimeHost runtime, ILogger<ServiceWorker> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _runtime.StartAsync(stoppingToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogCritical(ex, "Detection service worker stopped because startup failed."); throw; }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _runtime.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
