using System.Reflection;
using Npgsql;

namespace WebhookInbox.Core;

/// <summary>Applies the embedded SQL migrations in name order, once each.</summary>
public static class Migrator
{
    // Any fixed number works. It stops two processes migrating at the same time.
    private const long AdvisoryLockKey = 7_203_114;

    public static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        await Execute(connection, null, $"SELECT pg_advisory_lock({AdvisoryLockKey})", ct);
        try
        {
            await Execute(connection, null, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    name       text PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """, ct);

            foreach (var (name, sql) in LoadMigrations())
            {
                await using var transaction = await connection.BeginTransactionAsync(ct);

                await using var check = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE name = $1", connection, transaction);
                check.Parameters.AddWithValue(name);
                if (await check.ExecuteScalarAsync(ct) is not null)
                {
                    continue;
                }

                await Execute(connection, transaction, sql, ct);

                await using var record = new NpgsqlCommand("INSERT INTO schema_migrations (name) VALUES ($1)", connection, transaction);
                record.Parameters.AddWithValue(name);
                await record.ExecuteNonQueryAsync(ct);

                await transaction.CommitAsync(ct);
            }
        }
        finally
        {
            await Execute(connection, null, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static IEnumerable<(string Name, string Sql)> LoadMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            yield return (resource, reader.ReadToEnd());
        }
    }
}
