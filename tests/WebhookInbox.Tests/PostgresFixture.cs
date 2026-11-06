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

    /// <summary>Every integration test starts from empty tables.</summary>
    public async Task ResetAsync()
    {
        await using var command = DataSource.CreateCommand("TRUNCATE events, deliveries RESTART IDENTITY CASCADE");
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> CountAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
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
