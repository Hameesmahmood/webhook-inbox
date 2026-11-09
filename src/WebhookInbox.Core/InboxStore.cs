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

public sealed record EventSummary(long Id, string Source, string? IdempotencyKey, DateTime ReceivedAt, int SizeBytes);

public sealed record EventRecord(long Id, string Source, string? IdempotencyKey, DateTime ReceivedAt, byte[] Payload, string Headers);

public sealed record DeliveryRecord(
    long Id,
    long EventId,
    string Source,
    string Destination,
    string Status,
    int Attempts,
    DateTime NextAttemptAt,
    int? LastStatusCode,
    string? LastError,
    DateTime? DeliveredAt);

public sealed record InboxStats(long Pending, long Leased, long Delivered, long Dead, double? OldestDueSeconds);

public enum ReplayOutcome
{
    Replayed,
    NotFound,
    InProgress,
}

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

    // Reads for the admin API. Lists are newest first, paged by ID so new rows never shift a page.

    public async Task<IReadOnlyList<EventSummary>> ListEventsAsync(string? source, long? before, int take, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<EventSummary>(new CommandDefinition("""
            SELECT id AS Id, source AS Source, idempotency_key AS IdempotencyKey, received_at AS ReceivedAt,
                   octet_length(payload) AS SizeBytes
            FROM events
            WHERE (@Source::text IS NULL OR source = @Source)
              AND (@Before::bigint IS NULL OR id < @Before)
            ORDER BY id DESC
            LIMIT @Take
            """, new { Source = source, Before = before, Take = take }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<(EventRecord Event, IReadOnlyList<DeliveryRecord> Deliveries)?> GetEventAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var found = await connection.QuerySingleOrDefaultAsync<EventRecord>(new CommandDefinition("""
            SELECT id AS Id, source AS Source, idempotency_key AS IdempotencyKey, received_at AS ReceivedAt,
                   payload AS Payload, headers::text AS Headers
            FROM events
            WHERE id = @Id
            """, new { Id = id }, cancellationToken: ct));
        if (found is null)
        {
            return null;
        }

        var deliveries = await ListDeliveriesAsync(connection, null, null, null, found.Id, 1000, ct);
        return (found, deliveries);
    }

    public async Task<IReadOnlyList<DeliveryRecord>> ListDeliveriesAsync(string? status, string? source, long? before, int take, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ListDeliveriesAsync(connection, status, source, before, null, take, ct);
    }

    private static async Task<IReadOnlyList<DeliveryRecord>> ListDeliveriesAsync(
        NpgsqlConnection connection, string? status, string? source, long? before, long? eventId, int take, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<DeliveryRecord>(new CommandDefinition("""
            SELECT d.id AS Id, d.event_id AS EventId, e.source AS Source, d.destination AS Destination, d.status AS Status,
                   d.attempts AS Attempts, d.next_attempt_at AS NextAttemptAt, d.last_status_code AS LastStatusCode,
                   d.last_error AS LastError, d.delivered_at AS DeliveredAt
            FROM deliveries d
            JOIN events e ON e.id = d.event_id
            WHERE (@Status::text IS NULL OR d.status = @Status)
              AND (@Source::text IS NULL OR e.source = @Source)
              AND (@Before::bigint IS NULL OR d.id < @Before)
              AND (@EventId::bigint IS NULL OR d.event_id = @EventId)
            ORDER BY d.id DESC
            LIMIT @Take
            """, new { Status = status, Source = source, Before = before, EventId = eventId, Take = take }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<InboxStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleAsync<InboxStats>(new CommandDefinition("""
            SELECT count(*) FILTER (WHERE status = 'pending')   AS Pending,
                   count(*) FILTER (WHERE status = 'leased')    AS Leased,
                   count(*) FILTER (WHERE status = 'delivered') AS Delivered,
                   count(*) FILTER (WHERE status = 'dead')      AS Dead,
                   EXTRACT(EPOCH FROM now() - min(next_attempt_at)
                       FILTER (WHERE status = 'pending' AND next_attempt_at <= now()))::float8 AS OldestDueSeconds
            FROM deliveries
            """, cancellationToken: ct));
    }

    /// <summary>
    /// Sends a finished delivery again, with a fresh set of attempts. A delivery that's still
    /// pending or leased is refused, because replaying it would race the worker that holds it.
    /// </summary>
    public async Task<ReplayOutcome> ReplayAsync(long deliveryId, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var replayed = await connection.ExecuteAsync(new CommandDefinition($"""
            UPDATE deliveries
            SET {ResetForReplay}
            WHERE id = @Id AND status IN ('dead', 'delivered')
            """, new { Id = deliveryId }, cancellationToken: ct));
        if (replayed == 1)
        {
            return ReplayOutcome.Replayed;
        }

        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM deliveries WHERE id = @Id)", new { Id = deliveryId }, cancellationToken: ct));
        return exists ? ReplayOutcome.InProgress : ReplayOutcome.NotFound;
    }

    /// <summary>Replays every dead delivery for one source, for use after fixing the consumer.</summary>
    public async Task<int> ReplayDeadAsync(string source, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition($"""
            UPDATE deliveries d
            SET {ResetForReplay}
            FROM events e
            WHERE e.id = d.event_id AND e.source = @Source AND d.status = 'dead'
            """, new { Source = source }, cancellationToken: ct));
    }

    private const string ResetForReplay =
        "status = 'pending', attempts = 0, next_attempt_at = now(), last_status_code = NULL, last_error = NULL, delivered_at = NULL";

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
