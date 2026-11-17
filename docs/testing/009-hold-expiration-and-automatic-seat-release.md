# Verify US-07 locally

Run the Booking Engine with PostgreSQL and Kafka configured. The reconciliation worker starts automatically. For a restart check, stop Booking Engine after a hold is created, wait until its server-issued expiry time, and restart it. Verify that the hold reports `EXPIRED`, its segments can be held again, and Catalog projects them as available after consuming the expiry events. A confirmed hold keeps its allocations.

- Run `go test -race -tags integration ./...` from the Booking Engine directory with `BOOKING_TEST_DATABASE_URL` pointing to an isolated database and the local Kafka and Redis dependencies available. The expiry worker itself does not require Redis.
- Run `dotnet test tests/catalog --filter SeatProjectionKafkaConsumerTests` for the Catalog event consumer contract.
- Scrape `GET /metrics` on the private Booking Engine listener. `tetrail_booking_expiry_reconciled_total` counts committed due-hold reconciliations, including recovered legacy logical holds. `tetrail_booking_expiry_errors_total` counts failed batches. Both counters reset on process restart.

If the error counter rises, check PostgreSQL connectivity and lock contention. The worker retries and recovers committed holds after restart. If Booking reports expiry but Catalog remains stale, check unpublished seat events and Kafka consumer lag. Restore connectivity and let the outbox and projection consumers retry in order.
