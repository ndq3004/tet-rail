# Feature 005 Testing Runbook — Atomic Time-Limited Seat Holds

This runbook records the repeatable checks for Feature 005. It covers the implemented PostgreSQL hold lifecycle, transactional outbox, Kafka seat-state topic, and Catalog projection consumer.

The Booking Engine exposes `POST /api/holds`, `GET /api/booking-requests/{requestId}`, and `GET /api/holds/{holdId}`. A `202` confirms only durable command acceptance; poll until the request is `HELD` with a `hold_id` and `expires_at`, or `REJECTED`. The lifecycle is exercised by the Go integration test; the Kafka sample below is for manually testing the Catalog consumer contract.

## Scope and prerequisites

- Public booking requests go through the Gateway using a bearer token. The Gateway supplies the authenticated owner for submission and polling. Direct local Booking Engine requests in this runbook supply the trusted development owner header; use the same owner for both the submission and its lookups.

- Set `REDIS_URL` to enable shard snapshots. If a valid snapshot is unavailable, the worker replays from the earliest retained Kafka offset. PostgreSQL assigns each partition a new epoch and checks it in the transaction that applies a command.
- Run `go test -race -count=1 -tags=integration ./...` from the Booking Engine directory with local PostgreSQL, Kafka, and Redis available. The command tests cover concurrent retry during an ambiguous Kafka acknowledgement, stale-owner rejection, worker restart, and compensation after a conflicting segment.

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

## Hot-seat load workload

With Booking Engine running on port 8080, run the reproducible k6 workload from the repository root:

```powershell
k6 run -e VUS=20 -e DURATION=30s -e HOT_SEAT_RATIO=0.8 tests/load/booking-holds-hot-seat.js
```

Set `RUN_ID` when correlating an execution externally; otherwise the workload generates a unique run prefix for every invocation. It reports `hold_commands_accepted`, `hold_commands_rejected`, and `hold_commands_unexpected` separately. Record VUs, duration, payload shape, 12-partition configuration, hot-seat ratio, accepted/rejected ratio, acknowledgement latency, worker lag, and queue-drain time. This workload is evidence only after the HTTP endpoint and worker are running against the local Kafka/PostgreSQL platform.

## Development Kafka diagnostic endpoint

Set `ENABLE_KAFKA_DIAGNOSTICS=true` only for a local Booking Engine process. It enables `GET /internal/kafka/hold-commands`, which generates a unique sample hold command and submits it through the identical durable Kafka producer. It is intentionally absent unless explicitly enabled.

From `services/booking-engine`, dot-source `scripts/set-local-env.ps1` to set the local PostgreSQL URL, Kafka broker, and diagnostic flag in the current PowerShell session.

The integration-tagged Go suite uses the local PostgreSQL database and Kafka broker. It performs the following with generated UUIDs:

1. Creates an active hold for two segments.
2. Verifies the hold rows and unsent `SeatHeld.v1` outbox record commit together.
3. Confirms the hold and verifies two final allocations exist.
4. Creates an expired hold and verifies `ExpireDue` moves it to `EXPIRED`.
5. Produces a v1 seat-state event to Kafka and reads it back from the calculated key partition.
6. Creates a cross-carriage logical saga hold, verifies repeat worker delivery is harmless, then verifies a conflicting second shard compensates the first provisional hold.

To run the race detector later, use a Go toolchain with CGO enabled:

```powershell
go test -race ./...
```

## Manual durable hold command and Kafka inspection

This check proves the HTTP request is acknowledged only after the Booking Engine writes a `HoldRequested.v1` record to `booking.hold-commands.v1`. It uses generated test identifiers; do not reuse the sample idempotency key for a different payload.

### 1. Start the Booking Engine with local diagnostics

In a PowerShell terminal, run the Booking Engine against the local PostgreSQL and Kafka containers:

```powershell
Set-Location services/booking-engine
. .\scripts\set-local-env.ps1
go run ./cmd/api
```

Wait for the service to listen on port `8080`. `ENABLE_KAFKA_DIAGNOSTICS` is local-development only and is not required for the HTTP test below.

### 2. Submit a hold command

In a second PowerShell terminal, submit one same-carriage segment. The `202` response contains a `request_id`; it does not confirm that the hold succeeded.

```powershell
$request = @{
  segments = @(@{
    trip_id = "40000000-0000-0000-0000-000000000001"
    carriage_id = "A"
    seat_id = "70000000-0000-0000-0000-000000000001"
    segment_index = 1
  })
} | ConvertTo-Json -Depth 4

$headers = @{
  "Idempotency-Key" = [guid]::NewGuid().ToString()
  "X-User-Id" = "50000000-0000-0000-0000-000000000001"
  "Content-Type" = "application/json"
}

$accepted = Invoke-RestMethod -Method Post -Uri "http://localhost:8080/api/holds" -Headers $headers -Body $request
$accepted
```

Expected acknowledgement data:

```json
{ "request_id": "<uuid>", "status": "QUEUED" }
```

### 3. Inspect the Kafka command record and poll the result

Kafka receives a JSON `HoldRequested.v1` command keyed by `trip_id:carriage_id`. The worker fills the generated `request_id`, `owner_id`, `idempotency_key`, `correlation_id`, and ten-minute `expires_at` fields before producing it.

```powershell
docker exec tetrail-kafka-1 /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic booking.hold-commands.v1 --from-beginning --property print.key=true --property key.separator=" | " --timeout-ms 5000

Invoke-RestMethod -Method Get -Uri "http://localhost:8080/api/booking-requests/$($accepted.request_id)" -Headers $headers
```

Expected Kafka shape (the values are generated):

```text
40000000-0000-0000-0000-000000000001:A | {"request_id":"<uuid>","owner_id":"50000000-0000-0000-0000-000000000001","idempotency_key":"<uuid>","correlation_id":"<uuid>","expires_at":"<UTC timestamp>","segments":[{"trip_id":"40000000-0000-0000-0000-000000000001","carriage_id":"A","seat_id":"70000000-0000-0000-0000-000000000001","segment_index":1}]}
```

Poll until the response is `HELD` with `hold_id` and `expires_at`, or `REJECTED`. For a successful result, inspect the authoritative hold:

```powershell
$result = Invoke-RestMethod -Method Get -Uri "http://localhost:8080/api/booking-requests/$($accepted.request_id)" -Headers $headers
Invoke-RestMethod -Method Get -Uri "http://localhost:8080/api/holds/$($result.hold_id)" -Headers $headers
```

Repeat the identical request body and `Idempotency-Key`: it must return the original `request_id`. Change the body while keeping the key: it must return `409 Conflict`.

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
