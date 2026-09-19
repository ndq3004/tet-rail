CREATE TABLE IF NOT EXISTS catalog.seat_segment_projection_cursor (
    trip_id uuid NOT NULL REFERENCES catalog.trips(id) ON DELETE CASCADE,
    seat_id uuid NOT NULL REFERENCES catalog.seats(id),
    segment_index integer NOT NULL CHECK (segment_index >= 1),
    status varchar(10) NOT NULL CHECK (status IN ('AVAILABLE', 'HELD', 'SOLD')),
    source_position bigint NOT NULL CHECK (source_position >= 0),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (trip_id, seat_id, segment_index)
);

-- This cursor is retained through expiry so delayed SeatHeld events cannot recreate released availability.
