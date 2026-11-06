using Dapper;
using Npgsql;

namespace WebhookInbox.Core;

public sealed record NewEvent(string Source, string? IdempotencyKey, byte[] Payload, byte[] BodyHash, string HeadersJson);

public sealed record ClaimedDelivery(
    long Id,
    long EventId,
    string Destination,
    int Attempts,
    Guid LeaseToken,
    string Source,
    byte[] Payload,
    string Headers);

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

    /// <summary>
    /// Claims due deliveries. SKIP LOCKED means workers never wait for each other or take the same row.
    /// Attempts go up here and not after sending, so a message that crashes workers still runs out of attempts.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(int batchSize, TimeSpan lease, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var claimed = await connection.QueryAsync<ClaimedDelivery>(new CommandDefinition("""
            WITH next AS (
                SELECT id
                FROM deliveries
                WHERE status = 'pending' AND next_attempt_at <= now()
                ORDER BY next_attempt_at
                LIMIT @BatchSize
                FOR UPDATE SKIP LOCKED
            )
            UPDATE deliveries d
            SET status = 'leased',
                attempts = d.attempts + 1,
                lease_token = gen_random_uuid(),
                lease_expires_at = now() + make_interval(secs => @LeaseSeconds)
            FROM next, events e
            WHERE d.id = next.id AND e.id = d.event_id
            RETURNING d.id AS Id, d.event_id AS EventId, d.destination AS Destination, d.attempts AS Attempts,
                      d.lease_token AS LeaseToken, e.source AS Source, e.payload AS Payload, e.headers::text AS Headers
            """, new { BatchSize = batchSize, LeaseSeconds = lease.TotalSeconds }, cancellationToken: ct));
        return claimed.AsList();
    }

    /// <summary>
    /// Takes back jobs from workers that died. Jobs with no attempts left go to dead,
    /// which is what stops a message that crashes every worker that touches it.
    /// </summary>
    public async Task<int> SweepExpiredLeasesAsync(int maxAttempts, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
            WITH expired AS (
                SELECT id
                FROM deliveries
                WHERE status = 'leased' AND lease_expires_at <= now()
                FOR UPDATE SKIP LOCKED
            )
            UPDATE deliveries d
            SET status = CASE WHEN d.attempts >= @MaxAttempts THEN 'dead' ELSE 'pending' END,
                lease_token = NULL,
                lease_expires_at = NULL,
                next_attempt_at = now(),
                last_error = 'The lease expired before the worker finished.'
            FROM expired
            WHERE d.id = expired.id
            """, new { MaxAttempts = maxAttempts }, cancellationToken: ct));
    }

    // Each result below only applies while the lease token still matches. A worker that
    // lost its lease gets false back, and the newer state is left alone.

    public Task<bool> MarkDeliveredAsync(ClaimedDelivery delivery, int statusCode, CancellationToken ct = default) =>
        FinishAsync("""
            UPDATE deliveries
            SET status = 'delivered', delivered_at = now(), last_status_code = @StatusCode, last_error = NULL,
                lease_token = NULL, lease_expires_at = NULL
            WHERE id = @Id AND lease_token = @LeaseToken AND status = 'leased'
            """, new { delivery.Id, delivery.LeaseToken, StatusCode = statusCode }, ct);

    public Task<bool> MarkForRetryAsync(ClaimedDelivery delivery, TimeSpan delay, int? statusCode, string error, CancellationToken ct = default) =>
        FinishAsync("""
            UPDATE deliveries
            SET status = 'pending', next_attempt_at = now() + make_interval(secs => @DelaySeconds),
                last_status_code = @StatusCode, last_error = @Error,
                lease_token = NULL, lease_expires_at = NULL
            WHERE id = @Id AND lease_token = @LeaseToken AND status = 'leased'
            """, new { delivery.Id, delivery.LeaseToken, DelaySeconds = delay.TotalSeconds, StatusCode = statusCode, Error = error }, ct);

    public Task<bool> MarkDeadAsync(ClaimedDelivery delivery, int? statusCode, string error, CancellationToken ct = default) =>
        FinishAsync("""
            UPDATE deliveries
            SET status = 'dead', last_status_code = @StatusCode, last_error = @Error,
                lease_token = NULL, lease_expires_at = NULL
            WHERE id = @Id AND lease_token = @LeaseToken AND status = 'leased'
            """, new { delivery.Id, delivery.LeaseToken, StatusCode = statusCode, Error = error }, ct);

    private async Task<bool> FinishAsync(string sql, object parameters, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct)) == 1;
    }
}
