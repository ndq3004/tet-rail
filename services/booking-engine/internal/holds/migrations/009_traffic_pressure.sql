CREATE INDEX IF NOT EXISTS ix_booking_queued_requests ON booking.hold_requests(created_at) WHERE status='QUEUED';
