using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebhookInbox.Core;
using WebhookInbox.Worker;

namespace WebhookInbox.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AdminTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly InboxStore _store = new(postgres.DataSource);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task Sql(string sql)
    {
        await using var command = postgres.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong-key")]
    [InlineData("test-admin-key")]
    public async Task The_admin_api_needs_the_admin_key(string? authorization)
    {
        await using var api = TestApi.Create(postgres.ConnectionString);
        var client = api.CreateClient();
        if (authorization is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        }

        var response = await client.GetAsync("/v1/stats");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_does_not_need_the_admin_key()
    {
        await using var api = TestApi.Create(postgres.ConnectionString);

        var response = await api.CreateClient().PostAsync("/v1/hooks/plain", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Events_are_listed_newest_first_one_page_at_a_time()
    {
        for (var i = 0; i < 5; i++)
        {
            await Seed.EventAsync(_store);
        }

        await using var api = TestApi.Create(postgres.ConnectionString);
        var client = api.AdminClient();

        var ids = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Json(await client.GetAsync($"/v1/events?limit=2{(cursor is null ? "" : $"&cursor={cursor}")}"));
            ids.AddRange(page.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("id").GetString()!));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(["evt_5", "evt_4", "evt_3", "evt_2", "evt_1"], ids);
    }

    [Theory]
    [InlineData("/v1/events?limit=0")]
    [InlineData("/v1/events?limit=201")]
    [InlineData("/v1/events?cursor=nonsense")]
    [InlineData("/v1/deliveries?status=lost")]
    [InlineData("/v1/deliveries/replay")]
    public async Task Bad_queries_get_a_400_problem(string url)
    {
        await using var api = TestApi.Create(postgres.ConnectionString);
        var client = api.AdminClient();

        var response = url.EndsWith("/replay") ? await client.PostAsync(url, null) : await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_event_shows_its_body_headers_and_deliveries()
    {
        var id = await Seed.EventAsync(_store, """{"order":9}""", "http://one.test/hook", "http://two.test/hook");
        await using var api = TestApi.Create(postgres.ConnectionString);

        var e = await Json(await api.AdminClient().GetAsync($"/v1/events/evt_{id}"));

        Assert.Equal("""{"order":9}""", e.GetProperty("body").GetString());
        Assert.Equal("application/json", e.GetProperty("headers").GetProperty("Content-Type").GetString());
        Assert.Equal(2, e.GetProperty("deliveries").GetArrayLength());
        Assert.All(e.GetProperty("deliveries").EnumerateArray(), d => Assert.Equal("pending", d.GetProperty("status").GetString()));
    }

    [Theory]
    [InlineData("evt_999")]
    [InlineData("999")]
    [InlineData("dlv_1")]
    public async Task An_unknown_event_is_a_404(string id)
    {
        await Seed.EventAsync(_store);
        await using var api = TestApi.Create(postgres.ConnectionString);

        var response = await api.AdminClient().GetAsync($"/v1/events/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deliveries_can_be_filtered_by_status()
    {
        await Seed.EventAsync(_store);
        await Seed.EventAsync(_store);
        await Sql("UPDATE deliveries SET status = 'dead' WHERE id = 1");
        await using var api = TestApi.Create(postgres.ConnectionString);

        var page = await Json(await api.AdminClient().GetAsync("/v1/deliveries?status=dead"));

        var only = Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.Equal("dlv_1", only.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Replaying_a_dead_delivery_resets_it_to_pending()
    {
        await Seed.EventAsync(_store);
        await Sql("UPDATE deliveries SET status = 'dead', attempts = 8, last_error = 'gave up', last_status_code = 500");
        await using var api = TestApi.Create(postgres.ConnectionString);

        var response = await api.AdminClient().PostAsync("/v1/deliveries/dlv_1/replay", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'pending' AND attempts = 0 AND last_error IS NULL AND next_attempt_at <= now()"));
    }

    [Fact]
    public async Task A_delivered_delivery_can_be_replayed()
    {
        await Seed.EventAsync(_store);
        await Sql("UPDATE deliveries SET status = 'delivered', delivered_at = now()");
        await using var api = TestApi.Create(postgres.ConnectionString);

        var response = await api.AdminClient().PostAsync("/v1/deliveries/dlv_1/replay", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'pending' AND delivered_at IS NULL"));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("leased")]
    public async Task A_delivery_in_progress_cannot_be_replayed(string status)
    {
        await Seed.EventAsync(_store);
        if (status == "leased")
        {
            await _store.ClaimAsync(10, TimeSpan.FromMinutes(1));
        }

        await using var api = TestApi.Create(postgres.ConnectionString);

        var response = await api.AdminClient().PostAsync("/v1/deliveries/dlv_1/replay", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await postgres.CountAsync($"SELECT count(*) FROM deliveries WHERE status = '{status}'"));
    }

    [Theory]
    [InlineData("dlv_999")]
    [InlineData("evt_1")]
    public async Task Replaying_an_unknown_delivery_is_a_404(string id)
    {
        await Seed.EventAsync(_store);
        await using var api = TestApi.Create(postgres.ConnectionString);

        var response = await api.AdminClient().PostAsync($"/v1/deliveries/{id}/replay", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Bulk_replay_only_touches_dead_deliveries_from_that_source()
    {
        await Seed.EventAsync(_store);
        await Seed.EventAsync(_store);
        await Seed.EventAsync(_store);
        await Sql("""
            INSERT INTO events (source, body_hash, payload) VALUES ('other', '\x00', '\x00');
            INSERT INTO deliveries (event_id, destination, status) SELECT max(id), 'http://one.test/hook', 'dead' FROM events;
            UPDATE deliveries SET status = 'dead' WHERE id IN (1, 2);
            UPDATE deliveries SET status = 'delivered' WHERE id = 3;
            """);
        await using var api = TestApi.Create(postgres.ConnectionString);

        var result = await Json(await api.AdminClient().PostAsync("/v1/deliveries/replay?source=shop", null));

        Assert.Equal(2, result.GetProperty("replayed").GetInt32());
        Assert.Equal(2, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'pending'"));
        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'delivered'"));
        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'dead'"));
    }

    [Fact]
    public async Task Stats_count_deliveries_by_status()
    {
        for (var i = 0; i < 4; i++)
        {
            await Seed.EventAsync(_store);
        }

        await Sql("""
            UPDATE deliveries SET status = 'delivered' WHERE id = 1;
            UPDATE deliveries SET status = 'dead' WHERE id = 2;
            UPDATE deliveries SET next_attempt_at = now() - interval '30 seconds' WHERE id = 3;
            """);
        await using var api = TestApi.Create(postgres.ConnectionString);

        var stats = await Json(await api.AdminClient().GetAsync("/v1/stats"));

        Assert.Equal(2, stats.GetProperty("pending").GetInt64());
        Assert.Equal(0, stats.GetProperty("leased").GetInt64());
        Assert.Equal(1, stats.GetProperty("delivered").GetInt64());
        Assert.Equal(1, stats.GetProperty("dead").GetInt64());
        Assert.InRange(stats.GetProperty("oldestDueSeconds").GetDouble(), 29, 120);
    }

    [Fact]
    public async Task A_dead_delivery_is_delivered_after_the_consumer_is_fixed_and_it_is_replayed()
    {
        await Seed.EventAsync(_store);
        var broken = true;
        var destination = new FakeDestination((_, _) => Task.FromResult(new HttpResponseMessage(broken ? HttpStatusCode.NotFound : HttpStatusCode.OK)));
        var processor = new DeliveryProcessor(_store, new HttpClient(destination), Options.Create(new DeliveryOptions()), NullLogger<DeliveryProcessor>.Instance);

        await processor.RunOnceAsync();
        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'dead'"));

        broken = false;
        await using var api = TestApi.Create(postgres.ConnectionString);
        await api.AdminClient().PostAsync("/v1/deliveries/dlv_1/replay", null);
        await processor.RunOnceAsync();

        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'delivered' AND attempts = 1"));
        Assert.Equal(2, destination.Received.Count);
    }
}
