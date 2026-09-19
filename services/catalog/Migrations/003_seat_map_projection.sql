--BEGIN;

CREATE TABLE IF NOT EXISTS catalog.carriages (id uuid PRIMARY KEY, carriage_number varchar(10) NOT NULL UNIQUE, seat_class varchar(30) NOT NULL REFERENCES catalog.seat_classes(code));
CREATE TABLE IF NOT EXISTS catalog.seats (id uuid PRIMARY KEY, carriage_id uuid NOT NULL REFERENCES catalog.carriages(id), seat_number varchar(10) NOT NULL, seat_type varchar(30) NOT NULL, UNIQUE (carriage_id, seat_number));
CREATE TABLE IF NOT EXISTS catalog.trip_seats (trip_id uuid NOT NULL REFERENCES catalog.trips(id) ON DELETE CASCADE, seat_id uuid NOT NULL REFERENCES catalog.seats(id), PRIMARY KEY (trip_id, seat_id));
CREATE TABLE IF NOT EXISTS catalog.seat_segment_projection (trip_id uuid NOT NULL REFERENCES catalog.trips(id) ON DELETE CASCADE, seat_id uuid NOT NULL REFERENCES catalog.seats(id), segment_index integer NOT NULL CHECK (segment_index >= 1), status varchar(10) NOT NULL CHECK (status IN ('HELD', 'SOLD')), source_event_id uuid NOT NULL, source_position bigint NOT NULL CHECK (source_position >= 0), projection_version bigint NOT NULL CHECK (projection_version > 0), updated_at timestamptz NOT NULL, PRIMARY KEY (trip_id, seat_id, segment_index));
CREATE TABLE IF NOT EXISTS catalog.seat_projection_metadata (trip_id uuid PRIMARY KEY REFERENCES catalog.trips(id) ON DELETE CASCADE, data_version bigint NOT NULL DEFAULT 1 CHECK (data_version > 0));
CREATE TABLE IF NOT EXISTS catalog.seat_projection_inbox (event_id uuid PRIMARY KEY, source_position bigint NOT NULL CHECK (source_position >= 0), event_type varchar(100) NOT NULL, applied_at timestamptz NOT NULL);
CREATE INDEX IF NOT EXISTS ix_seat_segment_projection_lookup ON catalog.seat_segment_projection(trip_id, seat_id, segment_index);

INSERT INTO catalog.carriages (id, carriage_number, seat_class) VALUES ('60000000-0000-0000-0000-000000000001', '1', 'SOFT_SEAT'), ('60000000-0000-0000-0000-000000000002', '2', 'SLEEPER_4') ON CONFLICT DO NOTHING;
INSERT INTO catalog.seats (id, carriage_id, seat_number, seat_type) VALUES ('70000000-0000-0000-0000-000000000001', '60000000-0000-0000-0000-000000000001', '1A', 'WINDOW'), ('70000000-0000-0000-0000-000000000002', '60000000-0000-0000-0000-000000000001', '1B', 'AISLE'), ('70000000-0000-0000-0000-000000000003', '60000000-0000-0000-0000-000000000002', '1A', 'LOWER_BERTH'), ('70000000-0000-0000-0000-000000000004', '60000000-0000-0000-0000-000000000002', '1B', 'UPPER_BERTH') ON CONFLICT DO NOTHING;
INSERT INTO catalog.trip_seats (trip_id, seat_id) SELECT '40000000-0000-0000-0000-000000000001', id FROM catalog.seats ON CONFLICT DO NOTHING;
INSERT INTO catalog.seat_projection_metadata (trip_id, data_version) VALUES ('40000000-0000-0000-0000-000000000001', 1) ON CONFLICT DO NOTHING;

--COMMIT;

-- Roll back only before projection events are consumed. Afterwards use a forward migration and rebuild from retained Booking Engine events.
