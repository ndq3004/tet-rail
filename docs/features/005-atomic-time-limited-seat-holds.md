# 005 — Atomic time-limited seat holds

Status: `in-progress`
Owner: `Codex`
Depends on: `004`

## Outcome

The Booking Engine publishes durable, versioned seat-state events for successful holds, expirations, and confirmations. Catalog consumes them through an independent consumer group and atomically advances its inbox, segment cursor, and read projection before committing the Kafka offset.

## Scope

### In scope

- Define the `booking.seat-state.v1` topic, key, retention, and source-position convention for `SeatHeld.v1`, `HoldExpired.v1`, and `HoldConfirmed.v1`.
- Add a Booking Engine outbox publisher boundary that serializes the existing v1 JSON contract and uses Kafka record offsets as Catalog source positions.
- Add the Catalog hosted Kafka consumer with the `catalog-seat-projection-v1` consumer group, strict contract validation/deserialization, commit-after-apply semantics, bounded retries, and a DLQ.
- Add component tests for valid/deserialization-failure paths, retry/DLQ behavior, and no offset commit before a successful projection apply.

### Out of scope

- HTTP hold commands, the Booking Engine inventory state machine, PostgreSQL confirmed-allocation constraints, expiry scheduling, and cross-shard compensation. These require the still-unimplemented Booking persistence model and remain follow-up work within this feature.
- Replaying or operating production Kafka topics.

## Acceptance criteria

- [x] The canonical topic is `booking.seat-state.v1`; records are keyed by the stable `(trip_id, seat_id)` inventory identity and retained for seven days in local/default configuration.
- [x] `source_position` passed to Catalog equals the Kafka record offset; its ordering scope is explicitly the Kafka partition, while the Catalog cursor guards each seat segment.
- [x] Catalog validates and deserializes only the v1 contract before calling `ISeatProjectionEventApplier.ApplyAsync`.
- [x] Catalog commits a consumed offset only after `ApplyAsync` completes successfully, including duplicate or superseded transactional results.
- [x] Transient processing failures are retried with bounded exponential delay; terminal malformed or exhausted events are published to `catalog.seat-projection.v1.dlq` before their source offset is committed.
- [ ] Booking Engine hold persistence and transactional outbox rows publish the three state events only after their authoritative transition commits.

## Design and impact

- Service/module: Booking Engine owns production into `booking.seat-state.v1`; Catalog owns its `catalog-seat-projection-v1` group, DLQ publication, and projection application.
- API/event contract: `contracts/events/booking-seat-state.v1.schema.json` remains the canonical JSON value contract. Kafka record key is `trip_id:seat_id`; `source_position` is injected from Kafka metadata and is not trusted from a producer payload.
- Data/migration: Catalog uses its existing transactional inbox and segment cursor. No new Catalog migration is required. Booking transactional outbox persistence is pending the Booking state-model work; its forward-only migration will be added with that implementation.
- Security/observability: Consumer logs identifiers only as structured event metadata, never payloads containing future sensitive fields. Metrics cover apply, retry, DLQ, and committed offsets.
- Key decisions: A terminal bad record goes to DLQ to prevent poison-message partition blocking. A DLQ publish must succeed before committing the source offset. Kafka ordering is only per partition; cursor source positions protect per-segment projection correctness across delivery/replay.

## Implementation

- [x] Finalize topic/source-position semantics and document them in the contract knowledge.
- [x] Add Booking Engine Kafka event publisher and v1 envelope serializer boundary.
- [x] Add Catalog Kafka hosted consumer, bounded retry, DLQ publisher, and commit-after-transaction orchestration.
- [x] Add component tests for validation, retry/DLQ, and commit ordering.
- [ ] Implement Booking hold persistence, state transitions, transactional outbox, and authoritative event production.

## Verification

- [x] Unit/component: `dotnet test tests/catalog/TetRail.Catalog.Tests.csproj` — 16 passed.
- [x] Unit/component: `cd services/booking-engine; go test ./...` — all three packages passed.
- [ ] Integration/contract: Kafka-backed producer/consumer test against local Compose Kafka; validate v1 examples against the schema.
- [ ] Performance/race/security: `go test -race ./...` after Booking Engine concurrency implementation; N/A for the added Catalog consumer adapter.

## Progress notes

- 2026-09-19: Topic/source-position semantics and the consumer/producer boundaries were introduced with the existing Feature 004 applier. Booking state persistence remains the dependency for publishing authoritative events.

## Remaining risks

- The complete hold lifecycle is not yet implemented, so the producer boundary cannot be connected to a transactional Booking outbox until that state model and migration are added.
