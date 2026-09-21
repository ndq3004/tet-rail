CREATE TABLE IF NOT EXISTS catalog.trip_search_data_version (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    version bigint NOT NULL CHECK (version > 0)
);

INSERT INTO catalog.trip_search_data_version (singleton, version) VALUES (true, 1)
ON CONFLICT (singleton) DO NOTHING;

CREATE OR REPLACE FUNCTION catalog.bump_trip_search_data_version()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE catalog.trip_search_data_version SET version = version + 1 WHERE singleton = true;
    RETURN NULL;
END;
$$;

CREATE OR REPLACE TRIGGER trip_search_data_version_stations
AFTER INSERT OR UPDATE OR DELETE ON catalog.stations
FOR EACH STATEMENT EXECUTE FUNCTION catalog.bump_trip_search_data_version();
CREATE OR REPLACE TRIGGER trip_search_data_version_routes
AFTER INSERT OR UPDATE OR DELETE ON catalog.routes
FOR EACH STATEMENT EXECUTE FUNCTION catalog.bump_trip_search_data_version();
CREATE OR REPLACE TRIGGER trip_search_data_version_route_stops
AFTER INSERT OR UPDATE OR DELETE ON catalog.route_stops
FOR EACH STATEMENT EXECUTE FUNCTION catalog.bump_trip_search_data_version();
CREATE OR REPLACE TRIGGER trip_search_data_version_trips
AFTER INSERT OR UPDATE OR DELETE ON catalog.trips
FOR EACH STATEMENT EXECUTE FUNCTION catalog.bump_trip_search_data_version();
CREATE OR REPLACE TRIGGER trip_search_data_version_trip_stops
AFTER INSERT OR UPDATE OR DELETE ON catalog.trip_stops
FOR EACH STATEMENT EXECUTE FUNCTION catalog.bump_trip_search_data_version();
CREATE OR REPLACE TRIGGER trip_search_data_version_fares
AFTER INSERT OR UPDATE OR DELETE ON catalog.fares
FOR EACH STATEMENT EXECUTE FUNCTION catalog.bump_trip_search_data_version();

-- Rollback before production data: drop the six triggers, then the function and table.
-- After production data exists, use a forward-fix migration instead of destructive rollback.
