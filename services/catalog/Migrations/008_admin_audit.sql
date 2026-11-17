CREATE TABLE IF NOT EXISTS catalog.admin_audit (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    actor text NOT NULL,
    correlation_id uuid NOT NULL,
    resource text NOT NULL,
    resource_id uuid NOT NULL,
    operation text NOT NULL,
    before_state jsonb,
    after_state jsonb,
    occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE OR REPLACE FUNCTION catalog.bump_admin_seat_map()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    UPDATE catalog.seat_projection_metadata SET data_version=data_version+1, updated_at=now();
    RETURN NULL;
END;
$$;
CREATE TRIGGER admin_seat_cache_carriages AFTER INSERT OR UPDATE OR DELETE ON catalog.carriages
FOR EACH STATEMENT EXECUTE FUNCTION catalog.bump_admin_seat_map();
CREATE TRIGGER admin_seat_cache_seats AFTER INSERT OR UPDATE OR DELETE ON catalog.seats
FOR EACH STATEMENT EXECUTE FUNCTION catalog.bump_admin_seat_map();
CREATE TRIGGER admin_seat_cache_inventory AFTER INSERT OR UPDATE OR DELETE ON catalog.trip_seats
FOR EACH STATEMENT EXECUTE FUNCTION catalog.bump_admin_seat_map();
