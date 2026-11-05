namespace WebhookInbox.Worker;

/// <summary>Claims deliveries and sends them. The delivery loop arrives with the queue work.</summary>
public sealed class DeliveryWorker(ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker started");
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Worker stopping");
        }
    }
}
