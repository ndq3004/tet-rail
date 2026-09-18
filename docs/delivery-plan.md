# Delivery Plan — TetRail MVP

## Delivery principles

- Build an end-to-end vertical slice early: search → hold → order → simulated payment → ticket.
- Establish Booking and Payment correctness before optimizing throughput.
- Optimize from measurements; every milestone includes functional, concurrency, and observability checks.
- Keep five deployables; ticket, notification, and reporting workers remain modules of the fifth service for the MVP.

## Phases

### Phase 1 — Foundation

- Establish monorepo conventions, local orchestration, and CI.
- Standardize .NET for four business services and Go for the Booking Engine hot path.
- Provide local PostgreSQL, Redis, Kafka, object storage, and telemetry.
- Standardize API errors, correlation IDs, idempotency, outbox/inbox, and event envelopes.

### Phase 2 — Browse and booking correctness

- Complete identity, catalog, schedules, and seat-map projections.
- Build partition routing, durable booking commands, Go single-writer shard workers, TTL, snapshots/replay, and database constraints.
- Write concurrency tests before connecting payment.

### Phase 3 — Order, payment, and ticket

- Build the order state machine and payment simulator.
- Complete webhook idempotency, delayed callbacks, and compensation states.
- Issue QR/PDF tickets and add notification and reporting adapters.

### Phase 4 — High-CCU hardening

- Add rate limiting, waiting room, inventory-shard routing, cache, and end-to-end backpressure.
- Run distributed load tests for 1,000,000 durable booking command acknowledgements per second; report ingress acknowledgement, completion latency, queue lag, and success/reject ratio separately.
- Exercise Redis/broker restart, event replay, and duplicate delivery.

## Product backlog

### US-01 — Login and passenger management

Users can register/login and store passenger information. Support refresh/revoke, customer/admin roles, ownership checks, sensitive-ID validation/masking/protection, and passenger-change audit.

### US-02 — Trip and fare search

Users can search by origin, destination, and date. Results include schedule, duration, seat classes, versioned/effective fares, and approximate availability. Reads may degrade or return timestamped stale projections; Redis failures must not produce incorrect data.

### US-03 — Seat map and availability

Users can see `AVAILABLE`, `HELD`, and `SOLD` seats with class/type. Data is a timestamped projection; only hold creation confirms availability. Hold/confirm/expire events update or invalidate it.

### US-04 — Atomic expiring seat hold

`POST /holds` requires idempotency, durably appends a command, and returns `202` with `request_id`. Multi-seat operations are all-or-nothing. Routing is stable by trip and carriage/seat bucket; a Go logical single writer processes each partition. Holds have owner and expiry. Cross-shard requests use saga compensation. Load and hot-seat tests cover duplicates, restart, rebalance, and the PostgreSQL final constraint.

### US-05 — Order from hold

Orders snapshot fares, passengers, trip, seats, and payment expiry. One hold creates one logical order. Expired or foreign holds are rejected. State transitions, audit, and outbox are transactional.

### US-06 — Payment simulator and idempotent webhook

Support success, failure, timeout, delayed success, and duplicate callback. Webhooks require signatures and provider transaction IDs. Duplicates create no extra payment/ticket. Timely success leads to `PAID`; late success leads to `PAYMENT_REVIEW` and compensation.

### US-07 — Hold expiry and automatic release

Expiry uses server time, survives restart, is idempotent, and never releases confirmed bookings. Redis TTL is only a fast signal; reconciliation finds missed durable holds. Projection and metrics reflect expiry.

### US-08 — Electronic ticket issuance

Each order item has one active ticket. QR uses opaque/signed references and has validation. PDFs live in object storage behind expiring URLs. PDF/email failure retries and can enter DLQ without rolling back payment or booking.

### US-09 — Peak traffic control

Rate limits operate by IP, user, and endpoint and return `429` with retry guidance. Waiting-room tokens are signed, expiring, and replay-resistant. Activation uses saturation, queue depth, latency, and errors. Checkout receives a continuity policy. Load tests prove SLO protection or controlled shedding.

### US-10 — Administration, reporting, and operations

Admins manage stations, routes, trips, carriages, seats, and fares with validation/audit. Existing order snapshots never change. Reporting uses a read model and provides sales, occupancy, conversion, payment, latency, saturation, lag, DLQ, and active-hold views.

## Priority and dependencies

| Priority | Story | Dependencies | Main result |
|---:|---|---|---|
| 1 | US-02 | Foundation | Trip search |
| 2 | US-03 | US-02 | Seat map |
| 3 | US-04 | US-03 | Safe holds |
| 4 | US-01 | Foundation | Identity and passengers |
| 5 | US-05 | US-01, US-04 | Orders |
| 6 | US-06 | US-05 | Payment simulator |
| 7 | US-07 | US-04, US-05 | Automatic release |
| 8 | US-08 | US-06 | QR/PDF tickets |
| 9 | US-09 | Complete vertical slice | Peak protection |
| 10 | US-10 | Service domain events | Admin/reporting/operations |

## Required test strategy

- **Unit:** state machines, fare calculation, TTL rules, signatures, and idempotency.
- **Booking Engine:** `go test`, race detector, router/bitmap/segment benchmarks, snapshot replay, and goroutine-leak checks.
- **Integration:** PostgreSQL constraints, Kafka ordering/rebalance, Redis idempotency, outbox/inbox, retry, and DLQ.
- **Concurrency:** same-seat contention, all-or-nothing multi-seat, and expiry racing a payment callback.
- **Contract:** versioned HTTP/event schemas across five services.
- **End-to-end:** success, failure, timeout, duplicate callback, delayed success, and worker restart.
- **Load:** distributed million-command bursts, hot trip/seat, partition skew, payment burst, and soak; browse traffic is a secondary workload.
- **Resilience:** Redis/broker/database failover within the test environment's limits.

## Shared Definition of Done

- Acceptance criteria have corresponding automated tests.
- API/event contracts are versioned and have safe migration paths.
- Key failure modes have metrics, structured logs, traces, and runbooks.
- Logs contain no secrets, tokens, or complete PII.
- CI build, tests, and security checks pass.
- Booking Engine passes the Go race detector; contract tests prove Go/.NET payload compatibility.
- Hot-path changes include before/after benchmark or load-test evidence.
