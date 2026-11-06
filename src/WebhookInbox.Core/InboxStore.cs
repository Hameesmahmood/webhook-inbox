using Dapper;
using Npgsql;

namespace WebhookInbox.Core;

public sealed record NewEvent(string Source, string? IdempotencyKey, byte[] Payload, byte[] BodyHash, string HeadersJson);

public enum IngestOutcome
{
    Stored,
    Duplicate,
    KeyConflict,
}

public sealed record IngestResult(IngestOutcome Outcome, long EventId);

/// <summary>All SQL lives here. The locking and the transactions are what this project is about.</summary>
public sealed class InboxStore(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Saves the event and its deliveries in one transaction, so an event can't exist
    /// without its deliveries. The deliveries table is the transactional outbox.
    /// </summary>
    public async Task<IngestResult> IngestAsync(NewEvent newEvent, IReadOnlyList<string> destinations, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // If another request holds the same key, this waits for it to commit or roll back.
        var eventId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition("""
            INSERT INTO events (source, idempotency_key, body_hash, payload, headers)
            VALUES (@Source, @IdempotencyKey, @BodyHash, @Payload, @HeadersJson::jsonb)
            ON CONFLICT (source, idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING
            RETURNING id
            """, newEvent, transaction, cancellationToken: ct));

        if (eventId is null)
        {
            var existing = await connection.QuerySingleAsync<(long Id, byte[] BodyHash)>(new CommandDefinition("""
                SELECT id, body_hash FROM events WHERE source = @Source AND idempotency_key = @IdempotencyKey
                """, newEvent, transaction, cancellationToken: ct));

            var outcome = existing.BodyHash.AsSpan().SequenceEqual(newEvent.BodyHash)
                ? IngestOutcome.Duplicate
                : IngestOutcome.KeyConflict;
            return new IngestResult(outcome, existing.Id);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO deliveries (event_id, destination) VALUES (@EventId, @Destination)",
            destinations.Select(destination => new { EventId = eventId.Value, Destination = destination }).ToList(),
            transaction,
            cancellationToken: ct));

        await transaction.CommitAsync(ct);
        return new IngestResult(IngestOutcome.Stored, eventId.Value);
    }
}
