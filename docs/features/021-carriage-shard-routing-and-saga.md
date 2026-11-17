# 021 — Carriage shard routing and cross-shard hold saga

Status: `done`
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

- [x] Each segment has a carriage ID and commands route deterministically by it.
- [x] Multi-carriage commands either hold every shard or compensate all provisional holds.
- [x] Saga outcomes are idempotent, observable, and covered by tests.

## Design and impact

- Service/module: Booking owns the carriage route attached to a submitted segment; it does not query Catalog synchronously.
- API/event contract: mapping version `v1` is twelve Kafka partitions and key `trip_id:carriage_id`.
- Data/migration: routing metadata is forward-only; a partition count change needs a new mapping version.
- Key decisions: user-confirmed mapping on 2026-09-22 is `hash(trip_id + ":" + carriage_id) % 12`.

## Implementation

- [x] Commit the request outcome, saga state, provisional holds and compensation in one Booking PostgreSQL transaction. Retry infrastructure failures without converting them to contention rejections.

- [x] Add carriage-aware command validation/routing and migration metadata.
- [x] Implement saga coordinator, provisional holds, and compensation.
- [x] Add component and integration tests for logical saga holds, duplicate worker delivery, and compensation; recovery/rebalance remains in the next resilience slice.

## Verification

- [x] Unit/component: `cd services/booking-engine; go test ./...` — passed on 2026-09-25.
- [x] Integration: `go test -tags=integration ./...` — passed on 2026-09-25 against local PostgreSQL and Kafka.
- [x] Resilience: restart/rebalance and compensation checks on local Kafka/PostgreSQL.


## Progress notes

- 2026-09-25: A successful cross-carriage saga returns its own logical `hold_id`, which resolves through the regular hold lookup endpoint. The request reference permits either a physical or logical hold identity; physical reservation and confirmed-allocation constraints remain foreign-key enforced.
- 2026-09-25: PostgreSQL integration coverage proves that a multi-carriage command becomes `HELD` with a pollable logical hold, repeat delivery is harmless, and a later shard conflict compensates earlier provisional holds.
