# 007 — Order creation from an active hold

Status: `done`  
Owner: `Codex`  
Depends on: `006`, `005`

## Outcome

An authenticated customer creates one durable `PENDING_PAYMENT` order from an active hold they own.

## Scope

### In scope

- Versioned order HTTP contract and Gateway route.
- Order-owned PostgreSQL records, idempotency, transactional outbox, and immutable hold, seat, fare, and passenger snapshots.
- Booking Engine active-hold lookup using the trusted owner context.

### Out of scope

- Payment processing, hold confirmation, expiry reconciliation, ticket issuing, and refunds.

## Acceptance criteria

- [x] An active owned hold creates exactly one `PENDING_PAYMENT` order and payment deadline.
- [x] A duplicate idempotency key returns the original order. A changed request is rejected.
- [x] Expired, inactive, or foreign holds do not create an order.
- [x] Order items contain immutable trip, seat, fare, and passenger snapshots.
- [x] Order creation and `OrderCreated.v1` outbox insertion commit atomically.

## Design and impact

- Service/module: Order & Payment owns order, snapshot, idempotency, and outbox data in the `ordering` schema.
- API/event contract: `contracts/http/orders.v1.openapi.json` and `OrderCreated.v1`.
- Data/migration: forward-only migration. The initial price snapshot is submitted from the selected Catalog fare because the existing hold contract does not retain journey or fare selection. A later Catalog fare-resolution contract can replace this source without rewriting stored snapshots.
- Security/observability: Gateway derives customer identity from JWT. Order Service forwards the trusted owner context to Booking and does not log passenger values.
- Key decisions: one hold has one logical order, enforced by a unique `hold_id` constraint.

## Implementation

- [x] Add persistence and idempotent creation service.
- [x] Add Booking active-hold lookup and Gateway forwarding.
- [x] Add tests, contracts, knowledge, and runbook evidence.

## Verification

- [x] Build: `dotnet build services/order-payment/TetRail.OrderPayment.csproj --no-restore` and Gateway build passed.
- [x] Integration/contract: local Booking Engine, Kafka, and PostgreSQL created a `PENDING_PAYMENT` order from a held segment.
- [x] Security: a foreign owner received 404. Duplicate replay returned the original order and a changed payload returned 409.

## Progress notes

- 2026-11-13: Created from US-05 delivery requirements.
- 2026-11-13: Verified one `OrderCreated.v1` outbox row for the created order and persisted passenger/fare snapshots in PostgreSQL.

## Remaining risks

- The fare and passenger values are snapshotted from the selected order request because no authoritative Catalog lookup contract exists for the existing hold shape. Add Catalog resolution before production pricing.
