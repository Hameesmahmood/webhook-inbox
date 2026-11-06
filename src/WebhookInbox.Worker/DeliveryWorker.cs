using Microsoft.Extensions.Options;
using Npgsql;
using WebhookInbox.Core;

namespace WebhookInbox.Worker;

/// <summary>The delivery loop. It stops claiming on shutdown and lets jobs it holds finish.</summary>
public sealed class DeliveryWorker(
    DeliveryProcessor processor,
    InboxStore store,
    IOptions<DeliveryOptions> options,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var nextSweep = DateTimeOffset.MinValue;
        logger.LogInformation("Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextSweep)
                {
                    var recovered = await store.SweepExpiredLeasesAsync(settings.MaxAttempts, stoppingToken);
                    nextSweep = DateTimeOffset.UtcNow + settings.SweepInterval;
                    if (recovered > 0)
                    {
                        logger.LogWarning("Recovered {Count} deliveries with expired leases", recovered);
                    }
                }

                if (await processor.RunOnceAsync(stoppingToken) == 0)
                {
                    await Pause(settings.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (NpgsqlException ex)
            {
                // The database is the source of truth, so all a worker can do is wait for it.
                logger.LogError(ex, "The database is unavailable. Trying again shortly");
                await Pause(settings.PollInterval, stoppingToken);
            }
        }

        logger.LogInformation("Worker stopped");
    }

    private static async Task Pause(TimeSpan delay, CancellationToken ct) =>
        await Task.Delay(delay, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
}
