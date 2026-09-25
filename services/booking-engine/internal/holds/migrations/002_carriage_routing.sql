ALTER TABLE booking.held_segments ADD COLUMN IF NOT EXISTS carriage_id varchar(64);
UPDATE booking.held_segments SET carriage_id = 'legacy' WHERE carriage_id IS NULL;
ALTER TABLE booking.held_segments ALTER COLUMN carriage_id SET NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_booking_hold_sagas_request ON booking.hold_sagas(request_id);
