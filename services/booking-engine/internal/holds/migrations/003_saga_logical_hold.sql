ALTER TABLE booking.hold_sagas ADD COLUMN IF NOT EXISTS owner_id uuid;
ALTER TABLE booking.hold_sagas ADD COLUMN IF NOT EXISTS expires_at timestamptz;
UPDATE booking.hold_sagas request
SET owner_id = source.owner_id,
    expires_at = COALESCE(request.expires_at, request.created_at)
FROM booking.hold_requests source
WHERE request.request_id = source.id
  AND (request.owner_id IS NULL OR request.expires_at IS NULL);
ALTER TABLE booking.hold_sagas ALTER COLUMN owner_id SET NOT NULL;
ALTER TABLE booking.hold_sagas ALTER COLUMN expires_at SET NOT NULL;
