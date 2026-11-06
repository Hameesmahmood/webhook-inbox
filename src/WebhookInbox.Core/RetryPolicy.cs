namespace WebhookInbox.Core;

public enum DeliveryVerdict
{
    Delivered,
    Retry,
    Dead,
}

public static class RetryPolicy
{
    /// <summary>Decides what a response means. A null status means no response arrived at all.</summary>
    public static DeliveryVerdict Judge(int? statusCode) => statusCode switch
    {
        null => DeliveryVerdict.Retry,
        >= 200 and < 300 => DeliveryVerdict.Delivered,
        408 or 429 => DeliveryVerdict.Retry,
        >= 500 => DeliveryVerdict.Retry,
        _ => DeliveryVerdict.Dead,
    };

    /// <summary>
    /// Exponential backoff with full jitter: random(0, min(cap, base * 2^attempt)).
    /// A Retry-After from the consumer sets the minimum wait, up to the cap.
    /// </summary>
    public static TimeSpan NextDelay(int attempt, DeliveryOptions options, Random random, TimeSpan? retryAfter = null)
    {
        var exponent = Math.Min(Math.Max(attempt, 0), 30);
        var ceiling = Math.Min(options.MaxDelay.TotalSeconds, options.BaseDelay.TotalSeconds * Math.Pow(2, exponent));
        var delay = TimeSpan.FromSeconds(random.NextDouble() * ceiling);

        if (retryAfter is { } minimum)
        {
            var capped = minimum < options.MaxDelay ? minimum : options.MaxDelay;
            return delay > capped ? delay : capped;
        }

        return delay;
    }
}
