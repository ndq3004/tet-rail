# 012 — Administration, reporting, and operations

Status: `done`  
Owner: `Codex`  
Depends on: `008`, `009`, `010`

## Outcome

Authorized operators can maintain the schedule and inventory catalog and inspect asynchronous reporting views.

## Scope

- Catalog management and transaction audit. Event-driven reporting for sales, occupancy, conversion, payment, and operational signals.

## Acceptance criteria

- [x] Administrators can manage stations, routes, trips, carriages, seats, and fares with validation and transactional audit.
- [x] Catalog writes preserve existing order snapshots and refresh customer read versions.
- [x] Reporting projects domain events into its own read model and exposes sales, occupancy, conversion, payments, latency, saturation, lag, DLQ, and active holds.
- [x] Authorization, event replay, and rejected writes are verified.

## Design and impact

- Catalog owns administration. Ticketing owns reporting projections. Existing order snapshots and service databases remain isolated.
- Apply additive Catalog and Ticketing migrations. Forward-fix by retaining audit history and replaying report inputs.

## Implementation

- [x] Implement catalog management and audit.
- [x] Implement reporting projections and initial operational views.
- [x] Publish contracts and verification evidence.

## Verification

- 35 .NET regression tests passed with disposable PostgreSQL and live Redis. The Catalog matrix verified authorized writes and listing, invalid schedules, referential conflicts, transactional audit, and search-version updates. Gateway tests verified authorization order and forwarding with test upstreams.
- The separate live Kafka and PostgreSQL test verified DLQ projection and partition lag using a disposable topic. Replay tests verified sales deduplication and preservation of sold segments.
- The Booking Engine race suite passed. A rolled-back PostgreSQL fixture verified that one standalone hold and a two-carriage saga contributed two logical holds and three physical holds.

## Remaining risks

- Gateway verification used test authentication and stub upstreams. Deployed Cognito and production-scale reporting were outside the local verification.
