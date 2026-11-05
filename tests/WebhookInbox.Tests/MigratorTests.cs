using Npgsql;
using WebhookInbox.Core;

namespace WebhookInbox.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MigratorTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Creates_the_events_and_deliveries_tables()
    {
        await using var command = postgres.DataSource.CreateCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('events', 'deliveries')");

        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Concurrent_runs_apply_each_migration_once()
    {
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Migrator.MigrateAsync(postgres.DataSource)));

        await using var command = postgres.DataSource.CreateCommand(
            "SELECT count(*) FROM schema_migrations WHERE name LIKE '%001_init.sql'");

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_leased_row_must_have_a_lease()
    {
        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            WITH e AS (
                INSERT INTO events (source, body_hash, payload) VALUES ('test', '\x00', '\x00') RETURNING id
            )
            INSERT INTO deliveries (event_id, destination, status)
            SELECT id, 'http://example.test', 'leased' FROM e
            """, connection, transaction);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal("leased_rows_have_a_lease", error.ConstraintName);
    }
}
