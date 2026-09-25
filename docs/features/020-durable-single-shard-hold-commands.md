# 020 — Durable single-shard hold commands

Status: `in-progress`
Owner: `Codex`
Depends on: `005`

## Outcome

Customers can submit an idempotent, single-inventory-shard hold command. The Booking Engine durably appends it to Kafka before returning `202 Accepted`; a single Kafka consumer-group worker processes the command and exposes its eventual result through polling.

## Scope

### In scope

- Versioned `HoldRequested.v1` command contract and `booking.hold-commands.v1` Kafka topic.
- `POST /api/holds`, `GET /api/booking-requests/{requestId}`, and `GET /api/holds/{holdId}`.
- PostgreSQL-backed idempotency/result records and an ordered Kafka consumer-group worker which creates atomic same-shard holds.
- Component tests for request validation, idempotent enqueue/result behavior, and conflict rejection.
- Development-only HTTP diagnostics for publishing a validated hold command through the same Kafka producer.

### Out of scope

- Cross-shard seat selection: it requires the separately planned reservation saga/compensation slice.
- Snapshot/replay optimization, rebalance chaos testing, and distributed load evidence.

## Acceptance criteria

- [ ] A valid command with an `Idempotency-Key` and user context returns `202` only after Kafka acknowledges `HoldRequested.v1`.
- [ ] Repeating the same key and logical payload returns the original `request_id`; using it with a different payload is rejected.
- [ ] A command contains one to four seats for one journey and is accepted only when all seats map to one inventory shard.
- [ ] The worker stores `HELD` plus `hold_id`/`expires_at`, or `REJECTED`, and polling never exposes a false successful hold.
- [ ] Tests cover validation, duplicate submission, worker success, and contention rejection.

## Design and impact

- Service/module: Booking Engine owns request records and produces/consumes `booking.hold-commands.v1`; Kafka partitions provide one logical worker per assigned partition.
- API/event contract: add a versioned HTTP OpenAPI contract and `HoldRequested.v1` JSON command envelope. The Kafka key is the stable inventory-shard key derived from trip and seat bucket.
- Data/migration: a forward-only Booking migration creates request/idempotency/result records. A failed Kafka publish leaves no accepted result; a duplicate retry safely reuses the original request.
- Security/observability: owner identity comes from trusted gateway context, IDs are not logged as payloads, and request/worker outcomes are measured at their boundaries.
- Key decisions: cross-shard commands are explicitly rejected until saga compensation exists; direct Kafka acknowledgement is required before returning `202`.

## Implementation

- [x] Define command envelope, migration, and repository request lifecycle.
- [x] Add Kafka command publisher/consumer worker and HTTP transport.
- [x] Add HTTP/component coverage and update US-04 contract knowledge/runbook.
- [x] Add a development-only Kafka command diagnostic endpoint and manual verification steps.

## Verification

- [x] Unit/component: `cd services/booking-engine; go test ./...` — passed on 2026-09-25.
- [x] Integration/contract: `go test -tags=integration ./...` — passed on 2026-09-25 against local PostgreSQL and Kafka.
- [x] Performance/race/security: `go test -race ./...` — passed on 2026-09-25.

## Progress notes

- 2026-09-21: Split from Feature 005 so the durable single-shard command path can be implemented and reviewed independently.
- 2026-09-21: Added the Kafka command topic, stable trip-shard routing, PostgreSQL request/idempotency records, a consumer-group worker that creates a hold then records `HELD` or `REJECTED`, and HTTP enqueue/polling endpoints.
- 2026-09-23: Updated the hot-seat k6 workload for durable command acknowledgement and hot-seat contention coverage.
- 2026-09-25: Published the HTTP and command v1 schemas, aligned the transport with snake_case, made multi-carriage coordinator selection deterministic, and added component coverage for the public request shape.

## Remaining risks

- Cross-shard saga integration/recovery tests, snapshot/replay, rebalance, and load evidence remain required to close US-04.
