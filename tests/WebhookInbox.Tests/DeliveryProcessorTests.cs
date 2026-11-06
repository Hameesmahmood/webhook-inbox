using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebhookInbox.Core;
using WebhookInbox.Worker;

namespace WebhookInbox.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DeliveryProcessorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly InboxStore _store = new(postgres.DataSource);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private DeliveryProcessor Processor(FakeDestination destination, DeliveryOptions? options = null) => new(
        _store,
        new HttpClient(destination),
        Options.Create(options ?? new DeliveryOptions()),
        NullLogger<DeliveryProcessor>.Instance);

    private Task<long> Count(string status) =>
        postgres.CountAsync($"SELECT count(*) FROM deliveries WHERE status = '{status}'");

    [Fact]
    public async Task A_2xx_marks_the_delivery_as_delivered()
    {
        await Seed.EventAsync(_store);

        await Processor(FakeDestination.Returning(HttpStatusCode.OK)).RunOnceAsync();

        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'delivered' AND delivered_at IS NOT NULL AND last_status_code = 200"));
    }

    [Fact]
    public async Task Sends_the_original_body_and_the_webhook_headers()
    {
        var eventId = await Seed.EventAsync(_store, """{"order":7}""");
        var destination = FakeDestination.Returning(HttpStatusCode.OK);

        await Processor(destination).RunOnceAsync();

        var (request, body) = Assert.Single(destination.Received);
        Assert.Equal("""{"order":7}"""u8.ToArray(), body);
        Assert.Equal(new Uri("http://one.test/hook"), request.RequestUri);
        Assert.Equal($"evt_{eventId}", request.Headers.GetValues("Webhook-Id").Single());
        Assert.Equal("1", request.Headers.GetValues("Webhook-Attempt").Single());
        Assert.Equal("shop", request.Headers.GetValues("Webhook-Source").Single());
        Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Each_destination_gets_its_own_delivery()
    {
        await Seed.EventAsync(_store, "{}", "http://one.test/hook", "http://two.test/hook");
        var destination = FakeDestination.Returning(HttpStatusCode.OK);

        await Processor(destination).RunOnceAsync();

        Assert.Equal(2, destination.Received.Count);
        Assert.Equal(2, await Count("delivered"));
    }

    [Fact]
    public async Task A_500_is_retried_later()
    {
        await Seed.EventAsync(_store);

        await Processor(FakeDestination.Returning(HttpStatusCode.InternalServerError)).RunOnceAsync();

        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'pending' AND attempts = 1 AND last_status_code = 500 AND lease_token IS NULL"));
    }

    [Fact]
    public async Task A_404_goes_straight_to_dead()
    {
        await Seed.EventAsync(_store);

        await Processor(FakeDestination.Returning(HttpStatusCode.NotFound)).RunOnceAsync();

        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'dead' AND attempts = 1 AND last_status_code = 404"));
    }

    [Fact]
    public async Task Retry_after_pushes_the_next_attempt_back()
    {
        await Seed.EventAsync(_store);
        var destination = new FakeDestination((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });

        await Processor(destination).RunOnceAsync();

        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE next_attempt_at > now() + interval '100 seconds'"));
    }

    [Fact]
    public async Task A_connection_failure_is_retried()
    {
        await Seed.EventAsync(_store);
        var destination = new FakeDestination((_, _) => throw new HttpRequestException("connection refused"));

        await Processor(destination).RunOnceAsync();

        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'pending' AND last_status_code IS NULL AND last_error = 'connection refused'"));
    }

    [Fact]
    public async Task A_destination_that_hangs_times_out_and_is_retried()
    {
        await Seed.EventAsync(_store);
        var destination = new FakeDestination(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Processor(destination, new DeliveryOptions { HttpTimeout = TimeSpan.FromMilliseconds(100) }).RunOnceAsync();

        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'pending' AND last_error LIKE 'No response%'"));
    }

    [Fact]
    public async Task The_last_failed_attempt_goes_to_dead()
    {
        await Seed.EventAsync(_store);
        var options = new DeliveryOptions { MaxAttempts = 3, BaseDelay = TimeSpan.Zero };
        var processor = Processor(FakeDestination.Returning(HttpStatusCode.InternalServerError), options);

        for (var i = 0; i < 5; i++)
        {
            await processor.RunOnceAsync();
        }

        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'dead' AND attempts = 3"));
    }

    [Fact]
    public async Task An_event_fails_then_succeeds_on_retry()
    {
        await Seed.EventAsync(_store);
        var calls = 0;
        var destination = new FakeDestination((_, _) => Task.FromResult(new HttpResponseMessage(
            Interlocked.Increment(ref calls) < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)));
        var processor = Processor(destination, new DeliveryOptions { BaseDelay = TimeSpan.Zero });

        for (var i = 0; i < 3; i++)
        {
            await processor.RunOnceAsync();
        }

        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'delivered' AND attempts = 3"));
        Assert.Equal("3", destination.Received.Last().Request.Headers.GetValues("Webhook-Attempt").Single());
    }
}
