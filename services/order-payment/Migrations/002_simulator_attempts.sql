CREATE TABLE ordering.simulator_attempts (
    order_id uuid PRIMARY KEY REFERENCES ordering.orders(id),
    transaction_id uuid NOT NULL UNIQUE,
    idempotency_key varchar(256) NOT NULL,
    scenario text NOT NULL CHECK (scenario IN ('success','failure','timeout','delayed_success','duplicate_callback')),
    due_at timestamptz NOT NULL,
    correlation_id uuid NOT NULL,
    deliveries_remaining smallint NOT NULL CHECK (deliveries_remaining BETWEEN 0 AND 2),
    callback_outcome text CHECK (callback_outcome IN ('success','failure')),
    compensation_required boolean NOT NULL DEFAULT false,
    callback_received_at timestamptz
);

CREATE INDEX simulator_attempts_due ON ordering.simulator_attempts(due_at)
WHERE deliveries_remaining > 0;
