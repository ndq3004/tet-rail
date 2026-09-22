# 021 — Carriage shard routing and cross-shard hold saga

Status: `in-progress`
Owner: `Codex`
Depends on: `020`

## Outcome

Booking commands route deterministically by trip and carriage. Same-carriage holds remain single-partition operations; multi-carriage requests are coordinated with idempotent compensation.

## Scope

### In scope

- Add `carriage_id` to held segments and route with `hash(trip_id:carriage_id) % 12`.
- Persist mapping version metadata and define the saga/compensation states.

### Out of scope

- Dynamic partition-count migration and distributed performance certification.

## Acceptance criteria

- [ ] Each segment has a carriage ID and commands route deterministically by it.
- [ ] Multi-carriage commands either hold every shard or compensate all provisional holds.
- [ ] Saga outcomes are idempotent, observable, and covered by tests.

## Design and impact

- Service/module: Booking owns the carriage route attached to a submitted segment; it does not query Catalog synchronously.
- API/event contract: mapping version `v1` is twelve Kafka partitions and key `trip_id:carriage_id`.
- Data/migration: routing metadata is forward-only; a partition count change needs a new mapping version.
- Key decisions: user-confirmed mapping on 2026-09-22 is `hash(trip_id + ":" + carriage_id) % 12`.

## Implementation

- [ ] Add carriage-aware command validation/routing and migration metadata.
- [ ] Implement saga coordinator, provisional holds, and compensation.
- [ ] Add component/integration/recovery tests and update runbook.

## Verification

- [ ] Unit/component: `cd services/booking-engine; go test ./...`
- [ ] Integration: `go test -tags=integration ./...`
- [ ] Resilience: restart/rebalance and compensation checks on local Kafka/PostgreSQL.

## Remaining risks

- Snapshot/replay, race detector, and distributed load evidence remain after this slice.
