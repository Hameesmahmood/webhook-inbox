using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebhookInbox.Core;

namespace WebhookInbox.Worker;

/// <summary>Claims one batch, sends each delivery and records the result.</summary>
public sealed class DeliveryProcessor(
    InboxStore store,
    HttpClient http,
    IOptions<DeliveryOptions> options,
    ILogger<DeliveryProcessor> logger)
{
    private readonly DeliveryOptions _options = options.Value;

    public async Task<int> RunOnceAsync(CancellationToken stopping = default)
    {
        var claimed = await store.ClaimAsync(_options.BatchSize, _options.Lease, stopping);

        // Jobs already claimed aren't cancelled by shutdown. The HTTP timeout bounds them.
        await Task.WhenAll(claimed.Select(DeliverAsync));

        return claimed.Count;
    }

    private async Task DeliverAsync(ClaimedDelivery delivery)
    {
        var attempt = await SendAsync(delivery);

        var verdict = RetryPolicy.Judge(attempt.StatusCode);
        if (verdict == DeliveryVerdict.Retry && delivery.Attempts >= _options.MaxAttempts)
        {
            verdict = DeliveryVerdict.Dead;
        }

        var applied = verdict switch
        {
            DeliveryVerdict.Delivered => await store.MarkDeliveredAsync(delivery, attempt.StatusCode!.Value),
            DeliveryVerdict.Retry => await store.MarkForRetryAsync(
                delivery,
                RetryPolicy.NextDelay(delivery.Attempts, _options, Random.Shared, attempt.RetryAfter),
                attempt.StatusCode,
                attempt.Error),
            _ => await store.MarkDeadAsync(delivery, attempt.StatusCode, attempt.Error),
        };

        if (!applied)
        {
            logger.LogWarning("Delivery {DeliveryId} lost its lease, so this result was ignored", delivery.Id);
            return;
        }

        logger.LogInformation(
            "Delivery {DeliveryId} attempt {Attempt}: {Verdict} (status {StatusCode})",
            delivery.Id, delivery.Attempts, verdict, attempt.StatusCode);
    }

    private async Task<SendAttempt> SendAsync(ClaimedDelivery delivery)
    {
        using var timeout = new CancellationTokenSource(_options.HttpTimeout);
        using var request = BuildRequest(delivery);

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var statusCode = (int)response.StatusCode;
            return new SendAttempt(statusCode, ReadRetryAfter(response.Headers.RetryAfter), $"The destination returned {statusCode}.");
        }
        catch (OperationCanceledException)
        {
            return new SendAttempt(null, null, $"No response within {_options.HttpTimeout.TotalSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            return new SendAttempt(null, null, ex.Message);
        }
    }

    private static HttpRequestMessage BuildRequest(ClaimedDelivery delivery)
    {
        var content = new ByteArrayContent(delivery.Payload);
        content.Headers.TryAddWithoutValidation("Content-Type", ReadContentType(delivery.Headers));

        var request = new HttpRequestMessage(HttpMethod.Post, delivery.Destination) { Content = content };
        request.Headers.Add("Webhook-Id", PublicId.ForEvent(delivery.EventId));
        request.Headers.Add("Webhook-Attempt", delivery.Attempts.ToString());
        request.Headers.Add("Webhook-Source", delivery.Source);
        return request;
    }

    private static string ReadContentType(string headersJson)
    {
        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
        var match = headers?.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(match?.Value) ? "application/octet-stream" : match.Value.Value;
    }

    private static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }

        return null;
    }

    private sealed record SendAttempt(int? StatusCode, TimeSpan? RetryAfter, string Error);
}
