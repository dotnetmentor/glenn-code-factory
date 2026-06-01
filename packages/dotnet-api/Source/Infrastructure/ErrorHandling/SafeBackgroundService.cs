namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Abstract base class for background services that automatically catches exceptions,
/// enqueues them to the error pipeline, and retries after a delay.
/// Subclasses implement DoWorkAsync with their actual logic.
/// </summary>
public abstract class SafeBackgroundService : BackgroundService
{
    private readonly ErrorQueue _errorQueue;
    private readonly ILogger _logger;

    protected SafeBackgroundService(ErrorQueue errorQueue, ILogger logger)
    {
        _errorQueue = errorQueue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var serviceName = GetType().Name;
        _logger.LogInformation("{ServiceName} started", serviceName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DoWorkAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown, do not enqueue
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{ServiceName} encountered an error. Retrying in 5 seconds.", serviceName);

                try
                {
                    var errorEntry = new ErrorEntry(
                        Message: $"[{serviceName}] {ex.Message}",
                        StackTrace: ex.StackTrace,
                        Source: "BackgroundService",
                        Severity: "Error",
                        CorrelationId: null,
                        RequestPath: null,
                        RequestMethod: null,
                        ContextData: $"ServiceName: {serviceName}",
                        OccurredAt: DateTime.UtcNow
                    );

                    await _errorQueue.EnqueueAsync(errorEntry);
                }
                catch (Exception enqueueEx)
                {
                    _logger.LogError(enqueueEx, "Failed to enqueue error from {ServiceName}", serviceName);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("{ServiceName} stopped", serviceName);
    }

    /// <summary>
    /// Override this method to implement the actual background work.
    /// Exceptions thrown here will be caught, logged, and enqueued to the error pipeline.
    /// </summary>
    protected abstract Task DoWorkAsync(CancellationToken stoppingToken);
}
