using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using WebhookInbox.Core;
using WebhookInbox.Worker;

namespace WebhookInbox.Tests;

/// <summary>
/// Each test switches one concept off and shows what breaks, next to the real behaviour.
/// The naive versions exist only here. They must never move into production code.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConceptOffTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private readonly InboxStore _store = new(postgres.DataSource);

    public Task InitializeAsync() => postgres.ResetAsync();

    public async Task DisposeAsync() => await Sql("DROP TRIGGER IF EXISTS simulated_crash ON deliveries; DROP TRIGGER IF EXISTS simulated_crash ON events");

    private async Task Sql(string sql)
    {
        await using var command = postgres.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Makes every insert into a table fail, as if the process died at that point.</summary>
    private Task CrashOnInsertInto(string table) => Sql($"""
        CREATE OR REPLACE FUNCTION simulated_crash() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'simulated crash'; END $$;
        CREATE TRIGGER simulated_crash BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION simulated_crash();
        """);

    private static NewEvent Event(string body, string? key = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return new NewEvent("shop", key, bytes, SHA256.HashData(bytes), "{}");
    }

    private DeliveryProcessor Processor(FakeDestination destination, DeliveryOptions? options = null) => new(
        _store,
        new HttpClient(destination),
        Options.Create(options ?? new DeliveryOptions()),
        NullLogger<DeliveryProcessor>.Instance);

    [Fact]
    public async Task Store_first_off_acknowledges_an_event_that_was_never_saved()
    {
        await CrashOnInsertInto("events");

        // Naive: reply first, save afterwards.
        var replied = HttpStatusCode.Accepted;
        try
        {
            await _store.IngestAsync(Event("{}"), ["http://one.test/hook"]);
        }
        catch (PostgresException)
        {
            // The provider already has its 202, so nobody will ever send this again.
        }

        Assert.Equal(HttpStatusCode.Accepted, replied);
        Assert.Equal(0, await postgres.CountAsync("SELECT count(*) FROM events"));

        // Real: the same failure reaches the provider, which sends the event again.
        await using var api = TestApi.Create(postgres.ConnectionString);
        var response = await api.CreateClient().PostAsync("/v1/hooks/plain", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Outbox_off_leaves_an_event_nobody_will_ever_deliver()
    {
        await CrashOnInsertInto("deliveries");

        // Naive: two separate writes with no shared transaction.
        await Sql("""INSERT INTO events (source, body_hash, payload) VALUES ('shop', '\x00', '\x00')""");
        await Assert.ThrowsAsync<PostgresException>(() => Sql(
            "INSERT INTO deliveries (event_id, destination) SELECT id, 'http://one.test/hook' FROM events"));

        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM events e WHERE NOT EXISTS (SELECT 1 FROM deliveries d WHERE d.event_id = e.id)"));

        // Real: the failed delivery write takes the event with it, so nothing is half saved.
        await postgres.ResetAsync();
        await Assert.ThrowsAsync<PostgresException>(() => _store.IngestAsync(Event("{}"), ["http://one.test/hook"]));

        Assert.Equal(0, await postgres.CountAsync("SELECT count(*) FROM events"));
    }

    [Fact]
    public async Task Idempotency_off_delivers_a_resent_event_twice()
    {
        var destination = FakeDestination.Returning(HttpStatusCode.OK);

        // Naive: the key is ignored.
        await _store.IngestAsync(Event("{}", key: null), ["http://one.test/hook"]);
        await _store.IngestAsync(Event("{}", key: null), ["http://one.test/hook"]);
        await Processor(destination).RunOnceAsync();
        Assert.Equal(2, destination.Received.Count);

        // Real: the key stops the second copy at the door.
        await postgres.ResetAsync();
        destination.Received.Clear();
        await _store.IngestAsync(Event("{}", "evt-1"), ["http://one.test/hook"]);
        await _store.IngestAsync(Event("{}", "evt-1"), ["http://one.test/hook"]);
        await Processor(destination).RunOnceAsync();
        Assert.Single(destination.Received);
    }

    [Fact]
    public async Task Row_locks_off_lets_two_workers_claim_the_same_row()
    {
        await Seed.EventAsync(_store);
        const string naiveSelect = "SELECT id FROM deliveries WHERE status = 'pending' LIMIT 1";
        const string naiveClaim = "UPDATE deliveries SET status = 'leased', lease_token = gen_random_uuid(), lease_expires_at = now() + interval '1 minute' WHERE id = @Id";
        await using var workerA = await postgres.DataSource.OpenConnectionAsync();
        await using var workerB = await postgres.DataSource.OpenConnectionAsync();

        // Naive: both read before either writes, which is the race in slow motion.
        var a = await workerA.QuerySingleAsync<long>(naiveSelect);
        var b = await workerB.QuerySingleAsync<long>(naiveSelect);
        await workerA.ExecuteAsync(naiveClaim, new { Id = a });
        await workerB.ExecuteAsync(naiveClaim, new { Id = b });

        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Skip_locked_off_makes_workers_queue_behind_a_lock()
    {
        await Seed.EventAsync(_store);
        await Seed.EventAsync(_store);
        await using var holder = await postgres.DataSource.OpenConnectionAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("SELECT id FROM deliveries ORDER BY id LIMIT 1 FOR UPDATE", transaction: transaction);

        // Naive: a plain lock waits for the holder, here until the timeout gives up.
        await using var waiter = await postgres.DataSource.OpenConnectionAsync();
        await waiter.ExecuteAsync("SET lock_timeout = '200ms'");
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            waiter.ExecuteAsync("SELECT id FROM deliveries ORDER BY id LIMIT 1 FOR UPDATE"));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        await waiter.ExecuteAsync("SET lock_timeout = 0");

        // Real: the claim skips the held row and takes the next one straight away.
        var claimed = Assert.Single(await _store.ClaimAsync(10, Lease));
        Assert.Equal(2, claimed.Id);
    }

    [Fact]
    public async Task Leases_off_leaves_a_dead_workers_job_stuck_forever()
    {
        await Seed.EventAsync(_store);
        Assert.Single(await _store.ClaimAsync(10, Lease));
        await Sql("UPDATE deliveries SET lease_expires_at = now() - interval '1 hour'");

        // Naive: nothing takes the job back, so an hour later it's still unreachable.
        Assert.Empty(await _store.ClaimAsync(10, Lease));

        // Real: the sweep returns it to the queue.
        await _store.SweepExpiredLeasesAsync(maxAttempts: 8);
        Assert.Single(await _store.ClaimAsync(10, Lease));
    }

    [Fact]
    public async Task Lease_token_off_lets_a_slow_worker_overwrite_a_newer_result()
    {
        await Seed.EventAsync(_store);
        var slow = Assert.Single(await _store.ClaimAsync(10, Lease));
        await Sql("UPDATE deliveries SET lease_expires_at = now() - interval '1 second'");
        await _store.SweepExpiredLeasesAsync(maxAttempts: 8);
        var fast = Assert.Single(await _store.ClaimAsync(10, Lease));
        await _store.MarkDeliveredAsync(fast, 200);

        // Real: the late result is refused.
        Assert.False(await _store.MarkDeadAsync(slow, 500, "too late"));
        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'delivered'"));

        // Naive: matching on the ID alone wipes out a delivery that succeeded.
        await Sql($"UPDATE deliveries SET status = 'dead' WHERE id = {slow.Id}");
        Assert.Equal(0, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'delivered'"));
    }

    [Fact]
    public void Jitter_off_sends_every_retry_at_the_same_moment()
    {
        const int failures = 1000;
        var options = new DeliveryOptions { BaseDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromMinutes(5) };
        var random = new Random(42);

        // Naive: plain backoff gives everything that failed together the same delay.
        var plain = Enumerable.Repeat(options.BaseDelay.TotalSeconds * Math.Pow(2, 5), failures);
        var jittered = Enumerable.Range(0, failures).Select(_ => RetryPolicy.NextDelay(5, options, random).TotalSeconds);

        static int BusiestSecond(IEnumerable<double> delays) => delays.GroupBy(d => (int)d).Max(g => g.Count());

        Assert.Equal(failures, BusiestSecond(plain));
        Assert.True(BusiestSecond(jittered) < failures / 10, "Jitter should spread retries across many seconds.");
    }

    [Fact]
    public async Task Dead_letter_off_retries_a_poison_message_forever()
    {
        await Seed.EventAsync(_store, "poison");
        await Seed.EventAsync(_store, "healthy");
        var destination = new FakeDestination(async (request, ct) =>
            await request.Content!.ReadAsStringAsync(ct) == "poison"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK));

        // Naive: with no real limit, the poison message is still going after 20 rounds.
        var unlimited = Processor(destination, new DeliveryOptions { MaxAttempts = int.MaxValue, BaseDelay = TimeSpan.Zero });
        for (var i = 0; i < 20; i++)
        {
            await unlimited.RunOnceAsync();
        }

        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'pending' AND attempts = 20"));
        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'delivered'"));

        // Real: the same message stops at the limit, and the healthy one was never blocked.
        await Sql("UPDATE deliveries SET attempts = 0 WHERE status = 'pending'");
        var limited = Processor(destination, new DeliveryOptions { MaxAttempts = 3, BaseDelay = TimeSpan.Zero });
        for (var i = 0; i < 5; i++)
        {
            await limited.RunOnceAsync();
        }

        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'dead' AND attempts = 3"));
    }
}
