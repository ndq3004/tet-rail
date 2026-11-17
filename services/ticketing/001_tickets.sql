CREATE SCHEMA IF NOT EXISTS ticketing;
CREATE TABLE IF NOT EXISTS ticketing.inbox (
    id uuid PRIMARY KEY
);
CREATE TABLE IF NOT EXISTS ticketing.tickets (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    order_id uuid NOT NULL,
    item_id uuid NOT NULL UNIQUE,
    owner_id uuid NOT NULL,
    trip_id uuid NOT NULL,
    seat_id uuid NOT NULL,
    segment_index integer NOT NULL CHECK (segment_index > 0),
    qr_reference uuid NOT NULL UNIQUE DEFAULT gen_random_uuid(),
    issued_at timestamptz NOT NULL DEFAULT now(),
    pdf_ready boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ticket_order_owner ON ticketing.tickets(order_id,owner_id);
CREATE TABLE IF NOT EXISTS ticketing.jobs (
    ticket_id uuid NOT NULL REFERENCES ticketing.tickets(id),
    kind text NOT NULL CHECK (kind IN ('pdf','notification')),
    attempts integer NOT NULL DEFAULT 0,
    available_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    dead_at timestamptz,
    PRIMARY KEY(ticket_id,kind)
);
CREATE INDEX IF NOT EXISTS ticket_jobs_due ON ticketing.jobs(available_at) WHERE completed_at IS NULL AND dead_at IS NULL;
CREATE TABLE IF NOT EXISTS ticketing.notifications (
    ticket_id uuid PRIMARY KEY REFERENCES ticketing.tickets(id),
    delivered_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS ticketing.outbox (
    id uuid PRIMARY KEY,
    ticket_id uuid NOT NULL REFERENCES ticketing.tickets(id),
    body jsonb NOT NULL,
    published_at timestamptz
);

CREATE INDEX IF NOT EXISTS ticket_outbox_pending ON ticketing.outbox(id) WHERE published_at IS NULL;

CREATE SCHEMA IF NOT EXISTS reporting;
CREATE TABLE IF NOT EXISTS reporting.events (
    id uuid PRIMARY KEY,
    kind text NOT NULL,
    occurred_at timestamptz NOT NULL,
    body jsonb NOT NULL
);
CREATE INDEX IF NOT EXISTS reporting_events_kind ON reporting.events(kind,occurred_at);
CREATE TABLE IF NOT EXISTS reporting.occupancy (
    trip_id uuid NOT NULL,
    seat_id uuid NOT NULL,
    segment integer NOT NULL,
    status text NOT NULL,
    expires_at timestamptz,
    source_offset bigint NOT NULL,
    PRIMARY KEY(trip_id,seat_id,segment)
);
CREATE TABLE IF NOT EXISTS reporting.streams (
    topic text NOT NULL,
    partition_id integer NOT NULL,
    lag bigint NOT NULL,
    sampled_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(topic,partition_id)
);
