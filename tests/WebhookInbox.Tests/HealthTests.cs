using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace WebhookInbox.Tests;

[Collection(PostgresCollection.Name)]
public sealed class HealthTests(PostgresFixture postgres)
{
    private WebApplicationFactory<Program> Api() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(host => host.UseSetting("ConnectionStrings:Inbox", postgres.ConnectionString));

    [Fact]
    public async Task Healthz_reports_the_process_is_up()
    {
        await using var api = Api();

        var response = await api.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_is_ok_when_the_database_answers()
    {
        await using var api = Api();

        var response = await api.CreateClient().GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_is_503_when_the_database_is_gone()
    {
        // The API gets its own database, so dropping it here can't disturb other tests.
        await using (var create = postgres.DataSource.CreateCommand("CREATE DATABASE readyz_test"))
        {
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = "readyz_test" }.ConnectionString;
        await using var api = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseSetting("ConnectionStrings:Inbox", connectionString));
        var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/readyz")).StatusCode);

        await using (var drop = postgres.DataSource.CreateCommand("DROP DATABASE readyz_test WITH (FORCE)"))
        {
            await drop.ExecuteNonQueryAsync();
        }

        var response = await client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
