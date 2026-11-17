DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='booking.hold_sagas'::regclass AND conname='hold_sagas_lifecycle_state') THEN
        ALTER TABLE booking.hold_sagas DROP CONSTRAINT hold_sagas_state_check;
        ALTER TABLE booking.hold_sagas ADD CONSTRAINT hold_sagas_lifecycle_state
            CHECK (state IN ('PREPARING','HELD','COMPENSATING','COMPENSATED','REJECTED','EXPIRED','CONFIRMED'));
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS ix_booking_due_holds ON booking.holds(expires_at,id) WHERE state='ACTIVE';
CREATE INDEX IF NOT EXISTS ix_booking_due_sagas ON booking.hold_sagas(expires_at,id) WHERE state='HELD';
CREATE INDEX IF NOT EXISTS ix_booking_steps_hold_id ON booking.hold_saga_steps(hold_id);
CREATE INDEX IF NOT EXISTS ix_booking_unpublished_seat_events
    ON booking.outbox_events(event_key,occurred_at,id) WHERE published_at IS NULL;
