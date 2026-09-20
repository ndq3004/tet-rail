# Feature 005 Testing Runbook — Atomic Time-Limited Seat Holds

This runbook records the repeatable checks for Feature 005. It covers the implemented PostgreSQL hold lifecycle, transactional outbox, Kafka seat-state topic, and Catalog projection consumer.

The Booking Engine does **not** yet expose `POST /api/holds` or a booking-command ingress endpoint. Do not attempt to test hold creation through HTTP. The authoritative lifecycle is currently exercised by the Go integration test; the Kafka sample below is for manually testing the Catalog consumer contract.

## Scope and prerequisites

- Run from the repository root in PowerShell.
- Docker Desktop must be running.
- Go 1.24+ and .NET SDK 9 must be installed.
- Local Compose credentials are development-only values from `infrastructure/compose/.env.example`.

Start the stateful platform. The one-shot `kafka-init` job creates `booking.seat-state.v1` and `catalog.seat-projection.v1.dlq` with seven-day retention.

```powershell
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml up -d --wait
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml up kafka-init
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml ps
```

Expected: PostgreSQL, Kafka, and Redis are healthy; `kafka-init` exits with code 0.

## Automated verification

### Fast component suite

```powershell
Set-Location services/booking-engine
go test ./...
go test -tags=integration ./...
Set-Location ../..
dotnet test tests/catalog/TetRail.Catalog.Tests.csproj
```

The integration-tagged Go suite uses the local PostgreSQL database and Kafka broker. It performs the following with generated UUIDs:

1. Creates an active hold for two segments.
2. Verifies the hold rows and unsent `SeatHeld.v1` outbox record commit together.
3. Confirms the hold and verifies two final allocations exist.
4. Creates an expired hold and verifies `ExpireDue` moves it to `EXPIRED`.
5. Produces a v1 seat-state event to Kafka and reads it back from the calculated key partition.

To run the race detector later, use a Go toolchain with CGO enabled:

```powershell
go test -race ./...
```

## Manual Kafka-to-Catalog projection check

### 1. Start Catalog and apply its migrations

In one terminal, start Catalog with the local dependencies available:

```powershell
dotnet run --project services/catalog/TetRail.Catalog.csproj
```

In a second terminal, apply Catalog migrations once. This endpoint exists only in Development.

```powershell
Invoke-RestMethod -Method Post http://localhost:5000/internal/migrations/run
```

Use the actual Catalog port reported by `dotnet run` if it differs from `5000`. Keep Catalog running: its `catalog-seat-projection-v1` consumer group receives the event.

### 2. Sample event and Kafka key

Use a seeded Catalog trip and seat. The Kafka key must be exactly `trip_id:seat_id`.

```text
Key:
40000000-0000-0000-0000-000000000001:70000000-0000-0000-0000-000000000001
```

```json
{
  "event_id": "90000000-0000-0000-0000-000000000101",
  "event_type": "SeatHeld.v1",
  "version": 1,
  "occurred_at": "2026-09-19T12:00:00Z",
  "correlation_id": "90000000-0000-0000-0000-000000000102",
  "causation_id": "90000000-0000-0000-0000-000000000103",
  "payload": {
    "trip_id": "40000000-0000-0000-0000-000000000001",
    "seat_id": "70000000-0000-0000-0000-000000000001",
    "segment_indices": [1, 2]
  }
}
```

Start the local producer, paste one line in `key|json` form, then press Ctrl+Z followed by Enter to close stdin in PowerShell.

```powershell
docker exec -it tetrail-kafka-1 /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server localhost:9092 --topic booking.seat-state.v1 --property parse.key=true --property key.separator="|"
```

Example input line:

```text
40000000-0000-0000-0000-000000000001:70000000-0000-0000-0000-000000000001|{"event_id":"90000000-0000-0000-0000-000000000101","event_type":"SeatHeld.v1","version":1,"occurred_at":"2026-09-19T12:00:00Z","correlation_id":"90000000-0000-0000-0000-000000000102","causation_id":"90000000-0000-0000-0000-000000000103","payload":{"trip_id":"40000000-0000-0000-0000-000000000001","seat_id":"70000000-0000-0000-0000-000000000001","segment_indices":[1,2]}}
```

### 3. Track the result

Kafka ordering is per partition. Catalog injects the Kafka record offset as `source_position`, commits the offset only after its PostgreSQL projection transaction succeeds, and records `event_id` in its inbox.

Inspect the source topic:

```powershell
docker exec tetrail-kafka-1 /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic booking.seat-state.v1 --from-beginning --property print.key=true --property key.separator=" | " --timeout-ms 5000
```

Inspect the Catalog database after the consumer has processed the message:

```powershell
docker exec -it tetrail-postgres-1 psql -U tetrail -d tetrail -c "SELECT event_id, event_type, source_position, applied_at FROM catalog.seat_projection_inbox WHERE event_id = '90000000-0000-0000-0000-000000000101';"
docker exec -it tetrail-postgres-1 psql -U tetrail -d tetrail -c "SELECT trip_id, seat_id, segment_index, status, source_position FROM catalog.seat_segment_projection_cursor WHERE trip_id = '40000000-0000-0000-0000-000000000001' AND seat_id = '70000000-0000-0000-0000-000000000001' ORDER BY segment_index;"
docker exec -it tetrail-postgres-1 psql -U tetrail -d tetrail -c "SELECT trip_id, seat_id, segment_index, status FROM catalog.seat_segment_projection WHERE trip_id = '40000000-0000-0000-0000-000000000001' AND seat_id = '70000000-0000-0000-0000-000000000001' ORDER BY segment_index;"
```

Expected: an inbox row, cursor entries for segments 1 and 2 with `HELD`, and matching projection rows. Re-send the exact same event: the inbox prevents a second application. Send `HoldConfirmed.v1` with a new `event_id` and the same key/payload to expect `SOLD`.

## Booking lifecycle and outbox tracking

The integration test creates generated IDs, so capture IDs from SQL when diagnosing a local run rather than hard-coding them.

```powershell
docker exec -it tetrail-postgres-1 psql -U tetrail -d tetrail -c "SELECT id, state, expires_at, confirmed_at, expired_at FROM booking.holds ORDER BY created_at DESC LIMIT 20;"
docker exec -it tetrail-postgres-1 psql -U tetrail -d tetrail -c "SELECT hold_id, trip_id, seat_id, segment_index FROM booking.held_segments ORDER BY hold_id, segment_index;"
docker exec -it tetrail-postgres-1 psql -U tetrail -d tetrail -c "SELECT hold_id, trip_id, seat_id, segment_index, confirmed_at FROM booking.confirmed_allocations ORDER BY confirmed_at DESC LIMIT 20;"
docker exec -it tetrail-postgres-1 psql -U tetrail -d tetrail -c "SELECT id, event_type, event_key, occurred_at, published_at, attempts FROM booking.outbox_events ORDER BY occurred_at DESC LIMIT 20;"
```

Expected state transitions:

| Action | Hold | Reservation rows | Allocation rows | Outbox event |
|---|---|---|---|---|
| Create | `ACTIVE` | inserted | none | `SeatHeld.v1` per seat |
| Confirm before expiry | `CONFIRMED` | removed | inserted | `HoldConfirmed.v1` per seat |
| Expire | `EXPIRED` | removed | none | `HoldExpired.v1` per seat |

`published_at IS NULL` means Kafka dispatch is pending or retrying. A populated value means Kafka acknowledged the publisher; duplicate delivery remains safe because Catalog deduplicates by `event_id`.

## Failure-path checks

- Send malformed JSON or a payload missing a required field. Catalog must publish it to `catalog.seat-projection.v1.dlq` and only then commit the source offset.
- Send the same valid `event_id` twice. Exactly one inbox row and one effective projection transition must remain.
- Send `HoldConfirmed.v1`, then a later-delivered `HoldExpired.v1` with a higher Kafka offset. The cursor must retain `SOLD`; a confirmed seat is never made available by expiry.
- Attempt a duplicate confirmed `(trip_id, seat_id, segment_index)` allocation. PostgreSQL must reject it through the primary-key constraint in `booking.confirmed_allocations`.

## Cleanup

The manual SQL queries are read-only. Integration tests write development data. To remove all local development data only when intended:

```powershell
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml down --volumes
```

This deletes the local PostgreSQL, Kafka, Redis, and MinIO volumes.
