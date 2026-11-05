CREATE TABLE events (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source          text        NOT NULL,
    idempotency_key text,
    body_hash       bytea       NOT NULL,
    payload         bytea       NOT NULL,
    headers         jsonb       NOT NULL DEFAULT '{}',
    received_at     timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX events_source_idempotency_key_uq
    ON events (source, idempotency_key)
    WHERE idempotency_key IS NOT NULL;

CREATE TABLE deliveries (
    id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id         bigint      NOT NULL REFERENCES events (id),
    destination      text        NOT NULL,
    status           text        NOT NULL DEFAULT 'pending'
                     CHECK (status IN ('pending', 'leased', 'delivered', 'dead')),
    attempts         integer     NOT NULL DEFAULT 0,
    next_attempt_at  timestamptz NOT NULL DEFAULT now(),
    lease_token      uuid,
    lease_expires_at timestamptz,
    last_status_code integer,
    last_error       text,
    delivered_at     timestamptz,
    CONSTRAINT leased_rows_have_a_lease
        CHECK (status <> 'leased' OR (lease_token IS NOT NULL AND lease_expires_at IS NOT NULL))
);

CREATE INDEX deliveries_pending_idx ON deliveries (next_attempt_at) WHERE status = 'pending';
CREATE INDEX deliveries_leased_idx  ON deliveries (lease_expires_at) WHERE status = 'leased';
CREATE INDEX deliveries_event_idx   ON deliveries (event_id);
