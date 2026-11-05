using Npgsql;
using Testcontainers.PostgreSql;
using WebhookInbox.Core;

namespace WebhookInbox.Tests;

/// <summary>One real Postgres container shared by every integration test.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        await Migrator.MigrateAsync(DataSource);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
