# 009 — Hold expiration and automatic seat release

Status: `done`  
Owner: `Codex`  
Depends on: `005`, `007`

## Outcome

Expired holds release their seats automatically, including after Booking Engine restarts.

## Scope

### In scope

- PostgreSQL reconciliation, atomic logical-hold expiry, Catalog projection updates, and Booking Engine expiry metrics.

### Out of scope

- Payment deadline policy, refunds, and ticket delivery.

## Acceptance criteria

- [x] Server time controls expiry and a restarted worker catches up without Redis.
- [x] Concurrent workers release each logical hold once, including all active carriage steps.
- [x] Confirmation and expiry serialize. Confirmed allocations remain occupied.
- [x] Released segments are reusable and Catalog receives ordered HoldExpired.v1 events.
- [x] Metrics report committed expiry and reconciliation errors.

## Design and impact

- Service/module: Booking Engine owns reconciliation and allocation. Catalog consumes the existing seat-state events.
- API/event contract: Preserve HoldExpired.v1. Expose Prometheus-format expiry metrics on the private Booking Engine listener.
- Data/migration: Add partial due-hold and outbox indexes. Forward-fix by retaining existing hold and outbox data and replaying pending events after deployment.
- Security/observability: Metrics and retry logs omit individual hold identifiers.
- Key decisions: Scan PostgreSQL directly. Claim each logical hold transactionally and acquire physical hold and segment locks in the same order as payment confirmation.

## Implementation

- [x] Implement and start durable reconciliation.
- [x] Preserve per-seat outbox ordering through retries and publish expiry metrics.
- [x] Update project knowledge and verification instructions.

## Verification

- [x] Component: Metrics endpoint and bounded retry checks.
- [x] Integration/contract: PostgreSQL restart, concurrency, reuse, and outbox ordering checks. Catalog projection tests.
- [x] Race: Go race suite and expiry versus confirmation integration checks.

## Progress notes

- Booking Engine starts reconciliation immediately, scans once per second when idle, and drains in batches of 100.
- Go race and integration suites passed with an isolated PostgreSQL database. Focused tests passed for restart recovery, concurrent payment and expiry, segment reuse, and seat-event retry ordering. All 30 .NET tests passed, including the Catalog expiry-event contract and payment regressions.
- [Local verification and recovery instructions](../testing/009-hold-expiration-and-automatic-seat-release.md)

## Remaining risks

- None identified.
