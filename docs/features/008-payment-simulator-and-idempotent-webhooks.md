# 008 — Payment simulator and idempotent webhooks

Status: `done`  
Owner: `Codex`  
Depends on: `007`

## Outcome

Customers can exercise provider-style payments, and paid orders always have confirmed allocations.

## Scope

### In scope

- Success, failure, timeout, delayed success, and duplicate delivery.
- Signed callbacks, provider transaction deduplication, booking confirmation, and payment review.

### Out of scope

- Real provider integrations, real refunds, and ticket issuance.

## Acceptance criteria

- [x] The owner can schedule all five simulator scenarios and retry with the same idempotency key.
- [x] Invalid signatures and conflicting callbacks are rejected. Concurrent duplicate callbacks persist one outcome and event.
- [x] Timely success transitions to PAID only after Booking confirms every held segment.
- [x] Late success or unsuccessful allocation enters PAYMENT_REVIEW and records simulated compensation.
- [x] Failure and timeout reach the corresponding order state without requesting allocation.

## Design and impact

- Service/module: Gateway forwards owner context and callback signatures. Order & Payment owns attempts and callback results. Booking Engine owns allocation.
- API/event contract: payments v1 HTTP and PaymentAuthorized.v1, BookingConfirmed.v1, BookingConfirmationFailed.v1, PaymentSucceeded.v1, PaymentFailed.v1, OrderExpired.v1 LatePaymentReceived.v1, and PaymentReviewRequired.v1.
- Data/migration: Add durable attempts in ordering and confirmation results in booking. Forward-fix by pausing payment producers and consumers, retaining the rows and offsets, and replaying after deployment.
- Security/observability: HMAC-SHA256 over the exact body with a configured secret. Never log signatures or raw bodies.
- Key decisions: The simulator schedules delivery durably in PostgreSQL. A provider transaction ID is allocated per attempt. Late callbacks use the same intake path as immediate callbacks.

## Implementation

- [x] Implement signed simulator intake and durable callback processing.
- [x] Publish payment events and implement booking confirmation/result consumption.
- [x] Update contracts and project knowledge.

## Verification

- [x] Unit/component: Payment signature and HTTP contract checks.
- [x] Integration/contract: PostgreSQL-backed checks for all simulator scenarios, concurrent replay, and restart.
- [x] Performance/race/security: Booking Engine race suite and confirmation tests for duplicate, expired, and multi-carriage holds.

## Progress notes

- Payment authorization and order transitions commit with their outbox events. Booking stores the result with the complete logical-hold allocation and republishes it on replay.
- The live HTTP flow verified success, failure, duplicate delivery, invalid signatures, and signed callback replay across .NET, Kafka, Go, and PostgreSQL.
- Solution build and all 29 .NET tests passed. The full Go race and integration suite passed with an isolated PostgreSQL database.
- See the [local verification and recovery instructions](../testing/008-payment-simulator-and-idempotent-webhooks.md).

## Remaining risks

- The simulator requires a configured signing secret. Payment review records simulated compensation for reconciliation. Real refunds and ticket delivery are separate work.
