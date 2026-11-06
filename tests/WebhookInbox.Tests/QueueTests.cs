using WebhookInbox.Core;

namespace WebhookInbox.Tests;

[Collection(PostgresCollection.Name)]
public sealed class QueueTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private readonly InboxStore _store = new(postgres.DataSource);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task ExpireLeases()
    {
        await using var command = postgres.DataSource.CreateCommand(
            "UPDATE deliveries SET lease_expires_at = now() - interval '1 second' WHERE status = 'leased'");
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_claim_returns_the_event_and_starts_a_lease()
    {
        await Seed.EventAsync(_store, """{"order":1}""");

        var claimed = Assert.Single(await _store.ClaimAsync(10, Lease));

        Assert.Equal(1, claimed.Attempts);
        Assert.Equal("shop", claimed.Source);
        Assert.Equal("""{"order":1}"""u8.ToArray(), claimed.Payload);
        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'leased' AND lease_expires_at > now()"));
    }

    [Fact]
    public async Task A_claimed_row_is_not_claimed_again()
    {
        await Seed.EventAsync(_store);
        await _store.ClaimAsync(10, Lease);

        Assert.Empty(await _store.ClaimAsync(10, Lease));
    }

    [Fact]
    public async Task A_delivery_waiting_for_its_retry_time_is_not_claimed()
    {
        await Seed.EventAsync(_store);
        var claimed = Assert.Single(await _store.ClaimAsync(10, Lease));
        await _store.MarkForRetryAsync(claimed, TimeSpan.FromMinutes(5), 500, "failed");

        Assert.Empty(await _store.ClaimAsync(10, Lease));
    }

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_row()
    {
        for (var i = 0; i < 200; i++)
        {
            await Seed.EventAsync(_store);
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var mine = new List<long>();
            while (true)
            {
                var batch = await _store.ClaimAsync(7, Lease);
                if (batch.Count == 0)
                {
                    return mine;
                }

                mine.AddRange(batch.Select(d => d.Id));
            }
        }));

        var all = claims.SelectMany(ids => ids).ToList();
        Assert.Equal(200, all.Count);
        Assert.Equal(200, all.Distinct().Count());
    }

    [Fact]
    public async Task An_expired_lease_goes_back_to_pending_and_is_claimed_again()
    {
        await Seed.EventAsync(_store);
        var first = Assert.Single(await _store.ClaimAsync(10, Lease));
        await ExpireLeases();

        Assert.Equal(1, await _store.SweepExpiredLeasesAsync(maxAttempts: 8));

        var second = Assert.Single(await _store.ClaimAsync(10, Lease));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.Attempts);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
    }

    [Fact]
    public async Task A_live_lease_is_left_alone_by_the_sweep()
    {
        await Seed.EventAsync(_store);
        await _store.ClaimAsync(10, Lease);

        Assert.Equal(0, await _store.SweepExpiredLeasesAsync(maxAttempts: 8));
    }

    [Fact]
    public async Task A_message_that_keeps_killing_workers_ends_up_dead()
    {
        await Seed.EventAsync(_store);

        for (var i = 0; i < 3; i++)
        {
            Assert.Single(await _store.ClaimAsync(10, Lease));
            await ExpireLeases();
            await _store.SweepExpiredLeasesAsync(maxAttempts: 3);
        }

        Assert.Empty(await _store.ClaimAsync(10, Lease));
        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'dead'"));
    }

    [Fact]
    public async Task A_worker_that_lost_its_lease_cannot_overwrite_the_result()
    {
        await Seed.EventAsync(_store);
        var slow = Assert.Single(await _store.ClaimAsync(10, Lease));
        await ExpireLeases();
        await _store.SweepExpiredLeasesAsync(maxAttempts: 8);
        var fast = Assert.Single(await _store.ClaimAsync(10, Lease));

        Assert.True(await _store.MarkDeliveredAsync(fast, 200));
        Assert.False(await _store.MarkDeadAsync(slow, 404, "too late"));

        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'delivered' AND last_error IS NULL"));
    }
}
