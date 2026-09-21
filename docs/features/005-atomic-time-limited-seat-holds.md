# 005 — Atomic time-limited seat holds

Status: `in-progress`
Owner: `Codex`
Depends on: `004`

## Outcome

The Booking Engine publishes durable, versioned seat-state events for successful holds, expirations, and confirmations. Catalog consumes them through an independent consumer group and atomically advances its inbox, segment cursor, and read projection before committing the Kafka offset.

## Scope

### In scope

- Define the `booking.seat-state.v1` topic, key, retention, and source-position convention for `SeatHeld.v1`, `HoldExpired.v1`, and `HoldConfirmed.v1`.
- Add a Booking Engine PostgreSQL hold lifecycle, final confirmed-allocation constraint, and transactional outbox publisher that serializes the existing v1 JSON contract.
- Add the Catalog hosted Kafka consumer with the `catalog-seat-projection-v1` consumer group, strict contract validation/deserialization, commit-after-apply semantics, bounded retries, and a DLQ.
- Add component tests for valid/deserialization-failure paths, retry/DLQ behavior, and no offset commit before a successful projection apply.

### Out of scope

- HTTP hold commands, durable booking-command ingress, in-memory shard bitmaps/snapshots, and cross-shard compensation. These require the next delivery slice of US-04.
- Replaying or operating production Kafka topics.

## Acceptance criteria

- [x] The canonical topic is `booking.seat-state.v1`; records are keyed by the stable `(trip_id, seat_id)` inventory identity and retained for seven days in local/default configuration.
- [x] `source_position` passed to Catalog equals the Kafka record offset; its ordering scope is explicitly the Kafka partition, while the Catalog cursor guards each seat segment.
- [x] Catalog validates and deserializes only the v1 contract before calling `ISeatProjectionEventApplier.ApplyAsync`.
- [x] Catalog commits a consumed offset only after `ApplyAsync` completes successfully, including duplicate or superseded transactional results.
- [x] Transient processing failures are retried with bounded exponential delay; terminal malformed or exhausted events are published to `catalog.seat-projection.v1.dlq` before their source offset is committed.
- [x] Booking Engine hold persistence and transactional outbox rows publish the three state events only after their authoritative transition commits.
- [x] A PostgreSQL final constraint prevents duplicate confirmed `(trip_id, seat_id, segment_index)` allocations; expiry never releases confirmed allocations.

## Design and impact

- Service/module: Booking Engine owns production into `booking.seat-state.v1`; Catalog owns its `catalog-seat-projection-v1` group, DLQ publication, and projection application.
- API/event contract: `contracts/events/booking-seat-state.v1.schema.json` remains the canonical JSON value contract. Kafka record key is `trip_id:seat_id`; `source_position` is injected from Kafka metadata and is not trusted from a producer payload.
- Data/migration: Catalog uses its existing transactional inbox and segment cursor. Booking migration `internal/holds/migrations/001_hold_lifecycle.sql` creates the owned `booking` schema, holds, active held-segment reservations, final confirmed allocations, and outbox. It is forward-only; recovery is event replay from the seven-day topic retention window.
- Security/observability: Consumer logs identifiers only as structured event metadata, never payloads containing future sensitive fields. Metrics cover apply, retry, DLQ, and committed offsets.
- Key decisions: A terminal bad record goes to DLQ to prevent poison-message partition blocking. A DLQ publish must succeed before committing the source offset. Kafka ordering is only per partition; cursor source positions protect per-segment projection correctness across delivery/replay.

## Implementation

- [x] Finalize topic/source-position semantics and document them in the contract knowledge.
- [x] Add Booking Engine Kafka event publisher and v1 envelope serializer boundary.
- [x] Add Catalog Kafka hosted consumer, bounded retry, DLQ publisher, and commit-after-transaction orchestration.
- [x] Add component tests for validation, retry/DLQ, and commit ordering.
- [x] Implement Booking hold persistence, state transitions, transactional outbox, and authoritative event production.
- [x] Ensure the Catalog hosted consumer does not block application startup while waiting for a Kafka record.

## Verification

- [x] Unit/component: `dotnet test tests/catalog/TetRail.Catalog.Tests.csproj --no-restore` — 19 passed, including the non-blocking startup regression test.
- [x] Build: `dotnet build services/catalog/TetRail.Catalog.csproj --no-restore` — succeeded with 0 warnings and 0 errors.
- [x] Unit/component: `cd services/booking-engine; go test ./...` — all three packages passed.
- [x] Integration/contract: `go test -tags=integration ./...` against local Compose PostgreSQL and Kafka — passed.
- [ ] Performance/race/security: `go test -race ./...` — NOT RUN: this local Go toolchain has `CGO_ENABLED=0`, while the race detector requires CGO.

## Test runbook

See [`../testing/005-atomic-time-limited-seat-holds.md`](../testing/005-atomic-time-limited-seat-holds.md) for repeatable automated checks, manual Kafka injection, sample data, SQL tracking, and cleanup.

## Progress notes

- 2026-09-19: Topic/source-position semantics and the consumer/producer boundaries were introduced with the existing Feature 004 applier.
- 2026-09-19: Added PostgreSQL hold/segment/allocation/outbox persistence, outbox dispatch, local Kafka topic initialization, and PostgreSQL/Kafka integration tests. The final allocation constraint and outbox event are committed in the same transaction as a hold lifecycle transition.

## Remaining risks

- HTTP ingress, booking-command durability, shard ownership, and cross-shard saga compensation remain before US-04 is complete.
