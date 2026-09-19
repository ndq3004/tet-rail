

CREATE SCHEMA IF NOT EXISTS catalog;

CREATE TABLE IF NOT EXISTS catalog.stations (
    id uuid PRIMARY KEY,
    code varchar(10) NOT NULL UNIQUE,
    name varchar(120) NOT NULL,
    CONSTRAINT stations_code_uppercase CHECK (code = upper(code))
);

CREATE TABLE IF NOT EXISTS catalog.routes (
    id uuid PRIMARY KEY,
    code varchar(20) NOT NULL UNIQUE,
    name varchar(160) NOT NULL,
    data_version bigint NOT NULL DEFAULT 1 CHECK (data_version > 0)
);

CREATE TABLE IF NOT EXISTS catalog.route_stops (
    id uuid PRIMARY KEY,
    route_id uuid NOT NULL REFERENCES catalog.routes(id),
    station_id uuid NOT NULL REFERENCES catalog.stations(id),
    stop_order integer NOT NULL CHECK (stop_order >= 0),
    UNIQUE (route_id, stop_order),
    UNIQUE (route_id, station_id)
);

CREATE TABLE IF NOT EXISTS catalog.trips (
    id uuid PRIMARY KEY,
    route_id uuid NOT NULL REFERENCES catalog.routes(id),
    trip_number varchar(30) NOT NULL,
    status varchar(20) NOT NULL CHECK (status IN ('SCHEDULED', 'CANCELLED')),
    data_version bigint NOT NULL DEFAULT 1 CHECK (data_version > 0),
    UNIQUE (trip_number)
);

CREATE TABLE IF NOT EXISTS catalog.trip_stops (
    trip_id uuid NOT NULL REFERENCES catalog.trips(id) ON DELETE CASCADE,
    route_stop_id uuid NOT NULL REFERENCES catalog.route_stops(id),
    arrival_at timestamptz NOT NULL,
    departure_at timestamptz NOT NULL,
    PRIMARY KEY (trip_id, route_stop_id),
    CHECK (departure_at >= arrival_at)
);

CREATE TABLE IF NOT EXISTS catalog.seat_classes (
    code varchar(30) PRIMARY KEY,
    name varchar(100) NOT NULL
);

CREATE TABLE IF NOT EXISTS catalog.fares (
    id uuid PRIMARY KEY,
    route_id uuid NOT NULL REFERENCES catalog.routes(id),
    from_stop_order integer NOT NULL,
    to_stop_order integer NOT NULL,
    seat_class varchar(30) NOT NULL REFERENCES catalog.seat_classes(code),
    price numeric(12,2) NOT NULL CHECK (price >= 0),
    currency char(3) NOT NULL,
    version bigint NOT NULL CHECK (version > 0),
    effective_from timestamptz NOT NULL,
    effective_to timestamptz NULL,
    CHECK (from_stop_order < to_stop_order),
    CHECK (effective_to IS NULL OR effective_to > effective_from),
    UNIQUE (route_id, from_stop_order, to_stop_order, seat_class, version)
);

CREATE TABLE IF NOT EXISTS catalog.trip_availability_summary (
    trip_id uuid NOT NULL REFERENCES catalog.trips(id) ON DELETE CASCADE,
    seat_class varchar(30) NOT NULL REFERENCES catalog.seat_classes(code),
    status varchar(20) NOT NULL CHECK (status IN ('AVAILABLE', 'LIMITED', 'SOLD_OUT', 'UNKNOWN')),
    available_count integer NULL CHECK (available_count IS NULL OR available_count >= 0),
    as_of timestamptz NOT NULL,
    PRIMARY KEY (trip_id, seat_class)
);

CREATE INDEX IF NOT EXISTS ix_route_stops_station_route ON catalog.route_stops(station_id, route_id, stop_order);
CREATE INDEX IF NOT EXISTS ix_trip_stops_departure ON catalog.trip_stops(departure_at, trip_id);
CREATE INDEX IF NOT EXISTS ix_fares_lookup ON catalog.fares(route_id, from_stop_order, to_stop_order, effective_from, effective_to);



-- Rollback before production data:
-- DROP SCHEMA catalog CASCADE;
-- After production data exists, use a forward-fix migration instead of destructive rollback.
