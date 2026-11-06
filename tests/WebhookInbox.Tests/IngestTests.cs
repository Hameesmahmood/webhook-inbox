using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace WebhookInbox.Tests;

[Collection(PostgresCollection.Name)]
public sealed class IngestTests(PostgresFixture postgres) : IAsyncLifetime
{
    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static HttpRequestMessage Hook(string source, string body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/hooks/{source}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            request.Headers.Add("X-Event-Id", key);
        }

        return request;
    }

    private static async Task<string> IdOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

    [Fact]
    public async Task Stores_the_event_with_one_delivery_per_destination()
    {
        await using var api = TestApi.Create(postgres.ConnectionString);

        var response = await api.CreateClient().SendAsync(Hook("shop", """{"order":1}""", "a"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("evt_1", await IdOf(response));
        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM events"));
        Assert.Equal(2, await postgres.CountAsync("SELECT count(*) FROM deliveries WHERE status = 'pending'"));
    }

    [Fact]
    public async Task An_unknown_source_is_a_404_problem()
    {
        await using var api = TestApi.Create(postgres.ConnectionString);

        var response = await api.CreateClient().SendAsync(Hook("nope", "{}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_rejected_and_not_stored()
    {
        await using var api = TestApi.Create(postgres.ConnectionString);

        var response = await api.CreateClient().SendAsync(Hook("plain", new string('x', 1001)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await postgres.CountAsync("SELECT count(*) FROM events"));
    }

    [Fact]
    public async Task A_key_that_is_too_long_is_a_400()
    {
        await using var api = TestApi.Create(postgres.ConnectionString);

        var response = await api.CreateClient().SendAsync(Hook("shop", "{}", new string('k', 256)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_resent_event_is_stored_once_and_returns_the_original_id()
    {
        await using var api = TestApi.Create(postgres.ConnectionString);
        var client = api.CreateClient();

        var first = await client.SendAsync(Hook("shop", "{}", "same"));
        var second = await client.SendAsync(Hook("shop", "{}", "same"));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(await IdOf(first), await IdOf(second));
        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM events"));
        Assert.Equal(2, await postgres.CountAsync("SELECT count(*) FROM deliveries"));
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_a_conflict()
    {
        await using var api = TestApi.Create(postgres.ConnectionString);
        var client = api.CreateClient();

        await client.SendAsync(Hook("shop", """{"amount":1}""", "same"));
        var response = await client.SendAsync(Hook("shop", """{"amount":2}""", "same"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM events"));
    }

    [Fact]
    public async Task Keys_are_scoped_to_their_source()
    {
        await using var api = TestApi.Create(postgres.ConnectionString);
        await using var command = postgres.DataSource.CreateCommand("""
            INSERT INTO events (source, idempotency_key, body_hash, payload) VALUES ('other', 'same', '\x00', '\x00')
            """);
        await command.ExecuteNonQueryAsync();

        var response = await api.CreateClient().SendAsync(Hook("shop", "{}", "same"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_key_every_request_is_a_new_event()
    {
        await using var api = TestApi.Create(postgres.ConnectionString);
        var client = api.CreateClient();

        await client.SendAsync(Hook("plain", "{}"));
        await client.SendAsync(Hook("plain", "{}"));

        Assert.Equal(2, await postgres.CountAsync("SELECT count(*) FROM events"));
    }

    [Fact]
    public async Task Concurrent_resends_store_one_event()
    {
        await using var api = TestApi.Create(postgres.ConnectionString);
        var client = api.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.SendAsync(Hook("shop", "{}", "race"))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, await postgres.CountAsync("SELECT count(*) FROM events"));
        Assert.Equal(2, await postgres.CountAsync("SELECT count(*) FROM deliveries"));
    }

    [Fact]
    public async Task Credentials_are_not_stored_but_other_headers_are()
    {
        await using var api = TestApi.Create(postgres.ConnectionString);
        var request = Hook("plain", "{}");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
        request.Headers.TryAddWithoutValidation("Cookie", "session=secret");
        request.Headers.TryAddWithoutValidation("X-Custom", "kept");

        await api.CreateClient().SendAsync(request);

        await using var command = postgres.DataSource.CreateCommand("SELECT headers::text FROM events");
        var headers = (string)(await command.ExecuteScalarAsync())!;
        Assert.DoesNotContain("secret", headers);
        Assert.Contains("kept", headers);
    }

    [Fact]
    public async Task If_the_database_is_down_the_provider_is_told_to_send_again()
    {
        await using (var create = postgres.DataSource.CreateCommand("CREATE DATABASE ingest_down"))
        {
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = "ingest_down" }.ConnectionString;
        await using var api = TestApi.Create(connectionString);
        var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Hook("plain", "{}"))).StatusCode);

        await using (var drop = postgres.DataSource.CreateCommand("DROP DATABASE ingest_down WITH (FORCE)"))
        {
            await drop.ExecuteNonQueryAsync();
        }

        var response = await client.SendAsync(Hook("plain", "{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }
}
