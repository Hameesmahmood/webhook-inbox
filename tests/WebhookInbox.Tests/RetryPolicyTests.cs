using WebhookInbox.Core;

namespace WebhookInbox.Tests;

public sealed class RetryPolicyTests
{
    private static readonly DeliveryOptions Options = new()
    {
        BaseDelay = TimeSpan.FromSeconds(1),
        MaxDelay = TimeSpan.FromSeconds(60),
    };

    [Theory]
    [InlineData(200, DeliveryVerdict.Delivered)]
    [InlineData(204, DeliveryVerdict.Delivered)]
    [InlineData(301, DeliveryVerdict.Dead)]
    [InlineData(400, DeliveryVerdict.Dead)]
    [InlineData(401, DeliveryVerdict.Dead)]
    [InlineData(404, DeliveryVerdict.Dead)]
    [InlineData(408, DeliveryVerdict.Retry)]
    [InlineData(429, DeliveryVerdict.Retry)]
    [InlineData(500, DeliveryVerdict.Retry)]
    [InlineData(503, DeliveryVerdict.Retry)]
    [InlineData(null, DeliveryVerdict.Retry)]
    public void Judges_each_response(int? status, DeliveryVerdict expected) =>
        Assert.Equal(expected, RetryPolicy.Judge(status));

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 8)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(5000, 60)]
    public void Delay_stays_between_zero_and_the_ceiling(int attempt, double ceilingSeconds)
    {
        var random = new Random(1);

        for (var i = 0; i < 1000; i++)
        {
            var delay = RetryPolicy.NextDelay(attempt, Options, random);
            Assert.InRange(delay.TotalSeconds, 0, ceilingSeconds);
        }
    }

    [Fact]
    public void Retry_after_is_the_minimum_wait()
    {
        var delay = RetryPolicy.NextDelay(1, Options, new Random(1), TimeSpan.FromSeconds(30));

        Assert.True(delay >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Retry_after_cannot_exceed_the_cap()
    {
        var delay = RetryPolicy.NextDelay(1, Options, new Random(1), TimeSpan.FromDays(1));

        Assert.Equal(Options.MaxDelay, delay);
    }

    [Fact]
    public void A_lease_shorter_than_the_http_timeout_is_refused()
    {
        var options = new DeliveryOptions { HttpTimeout = TimeSpan.FromSeconds(10), Lease = TimeSpan.FromSeconds(10) };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
