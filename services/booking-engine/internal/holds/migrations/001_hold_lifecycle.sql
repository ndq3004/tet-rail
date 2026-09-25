CREATE SCHEMA IF NOT EXISTS booking;
CREATE TABLE IF NOT EXISTS booking.holds (
  id uuid PRIMARY KEY, owner_id uuid NOT NULL, state varchar(16) NOT NULL CHECK (state IN ('ACTIVE','CONFIRMED','EXPIRED','CANCELLED')),
  expires_at timestamptz NOT NULL, confirmed_at timestamptz NULL, expired_at timestamptz NULL, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS booking.held_segments (
  hold_id uuid NOT NULL REFERENCES booking.holds(id) ON DELETE CASCADE, trip_id uuid NOT NULL, carriage_id varchar(64) NOT NULL, seat_id uuid NOT NULL, segment_index integer NOT NULL CHECK(segment_index > 0),
  PRIMARY KEY (trip_id,seat_id,segment_index), UNIQUE (hold_id,trip_id,seat_id,segment_index)
);
CREATE TABLE IF NOT EXISTS booking.confirmed_allocations (
  trip_id uuid NOT NULL, seat_id uuid NOT NULL, segment_index integer NOT NULL CHECK(segment_index > 0), hold_id uuid NOT NULL REFERENCES booking.holds(id), confirmed_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (trip_id,seat_id,segment_index)
);
CREATE TABLE IF NOT EXISTS booking.outbox_events (
  id uuid PRIMARY KEY, event_type varchar(64) NOT NULL, event_key varchar(160) NOT NULL, payload jsonb NOT NULL,
  occurred_at timestamptz NOT NULL DEFAULT now(), published_at timestamptz NULL, locked_until timestamptz NULL, attempts integer NOT NULL DEFAULT 0 CHECK(attempts >= 0), available_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_booking_outbox_pending ON booking.outbox_events(available_at, occurred_at) WHERE published_at IS NULL;
CREATE TABLE IF NOT EXISTS booking.hold_requests (
  id uuid PRIMARY KEY, owner_id uuid NOT NULL, idempotency_key varchar(256) NOT NULL, payload jsonb NOT NULL,
  status varchar(16) NOT NULL CHECK(status IN ('QUEUED','HELD','REJECTED')), hold_id uuid NULL REFERENCES booking.holds(id), expires_at timestamptz NULL, created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(owner_id,idempotency_key)
);
CREATE TABLE IF NOT EXISTS booking.hold_sagas (
  id uuid PRIMARY KEY, request_id uuid NOT NULL UNIQUE REFERENCES booking.hold_requests(id), state varchar(24) NOT NULL CHECK(state IN ('PREPARING','HELD','COMPENSATING','COMPENSATED','REJECTED')),
  mapping_version varchar(16) NOT NULL, owner_id uuid NOT NULL, expires_at timestamptz NOT NULL, created_at timestamptz NOT NULL DEFAULT now(), completed_at timestamptz NULL
);
CREATE TABLE IF NOT EXISTS booking.hold_saga_steps (
  saga_id uuid NOT NULL REFERENCES booking.hold_sagas(id) ON DELETE CASCADE, shard_key varchar(160) NOT NULL, hold_id uuid NULL REFERENCES booking.holds(id), state varchar(24) NOT NULL CHECK(state IN ('PENDING','HELD','COMPENSATED','REJECTED')),
  PRIMARY KEY(saga_id, shard_key)
);
