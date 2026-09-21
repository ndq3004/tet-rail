ALTER TABLE catalog.seat_projection_metadata
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

UPDATE catalog.seat_projection_metadata
SET updated_at = now()
WHERE updated_at IS NULL;

-- Rollback before projection events are consumed:
-- ALTER TABLE catalog.seat_projection_metadata DROP COLUMN IF EXISTS updated_at;
-- Afterwards use a forward-fix migration and replay/rebuild from retained events.
