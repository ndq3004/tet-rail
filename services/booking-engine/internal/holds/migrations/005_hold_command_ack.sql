ALTER TABLE booking.hold_requests ADD COLUMN IF NOT EXISTS command_envelope jsonb;
ALTER TABLE booking.hold_requests ADD COLUMN IF NOT EXISTS acknowledged_at timestamptz;
