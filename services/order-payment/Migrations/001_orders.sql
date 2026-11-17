CREATE SCHEMA IF NOT EXISTS ordering;
CREATE TABLE IF NOT EXISTS ordering.schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS ordering.orders (
  id uuid PRIMARY KEY, hold_id uuid NOT NULL UNIQUE, owner_id uuid NOT NULL,
  idempotency_key varchar(256) NOT NULL, request_fingerprint text NOT NULL,
  state varchar(32) NOT NULL CHECK (state IN ('PENDING_PAYMENT','PAID','PAYMENT_FAILED','EXPIRED','CANCELLED','PAYMENT_REVIEW')),
  total_amount numeric(12,2) NOT NULL CHECK (total_amount >= 0), currency char(3) NOT NULL,
  payment_expires_at timestamptz NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(owner_id,idempotency_key)
);
CREATE TABLE IF NOT EXISTS ordering.order_items (
  id uuid PRIMARY KEY, order_id uuid NOT NULL REFERENCES ordering.orders(id) ON DELETE CASCADE,
  trip_id uuid NOT NULL, seat_id uuid NOT NULL, segment_index integer NOT NULL,
  passenger_snapshot jsonb NOT NULL, fare_snapshot jsonb NOT NULL,
  UNIQUE(order_id, trip_id, seat_id, segment_index)
);
CREATE TABLE IF NOT EXISTS ordering.outbox_events (
  id uuid PRIMARY KEY, event_type varchar(64) NOT NULL, aggregate_id uuid NOT NULL,
  correlation_id uuid NOT NULL, payload jsonb NOT NULL, occurred_at timestamptz NOT NULL DEFAULT now(),
  published_at timestamptz NULL
);
