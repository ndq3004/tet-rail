# Contract Knowledge

Primary sources: [`../product-brief.md`](../product-brief.md) and [`../architecture.md`](../architecture.md). This is a preliminary inventory and does not replace OpenAPI or AsyncAPI schemas when they are introduced.

Quick reference: [`../api-overview.md`](../api-overview.md) separates implemented endpoints from approved but not-yet-implemented contracts across all services.

In local Development runs, the four .NET services expose generated OpenAPI at `/openapi/v1.json` and Swagger UI at `/swagger`. The Aspire dashboard provides a Swagger UI link for each; the Go Booking Engine has no OpenAPI UI until it publishes an OpenAPI document.

## HTTP

| Method/path | Core contract |
|---|---|
| `GET /api/trips?from=&to=&date=` | Searches trips; data may come from a projection or cache. |
| `GET /api/trips/{tripId}/seats` | Returns the seat-availability projection with its timestamp/version. |
| `POST /api/holds` | Requires `Idempotency-Key`; returns `202` and `request_id` after durable append. |
| `GET /api/booking-requests/{requestId}` | Returns a processing status such as `HELD` or `REJECTED`. |
| `GET /api/holds/{holdId}` | Returns a hold and its `expires_at`. |
| `POST /api/orders` | Creates an order from an active hold. |
| `POST /api/payments/{orderId}/simulate` | Starts a payment simulator scenario. |
| `POST /api/payment-webhooks/simulator` | Receives signed, deduplicated simulator webhooks. |
| `GET /api/orders/{orderId}` | Reads order/payment status. |
| `GET /api/tickets/{ticketId}` | Reads authorized ticket metadata or resources. |
| `GET /api/identity/me` | Returns the authenticated Cognito subject and application roles. |
| `GET/POST/PATCH/DELETE /api/passengers[/{passengerId}]` | Authenticated customer passenger-profile operations; Catalog enforces ownership. |

## Events

- Foundational events: `SeatHeld.v1`, `HoldExpired.v1`, `OrderCreated.v1`, `PaymentSucceeded.v1`, `PaymentFailed.v1`, `BookingConfirmed.v1`, and `TicketIssued.v1`.
- Minimum envelope: `event_id`, `event_type`, `version`, `occurred_at`, `correlation_id`, `causation_id`, and a versioned payload.
- Producers and consumers must tolerate repeated delivery through outbox/inbox, idempotency, and unique business keys.
- A breaking change creates a new version; do not change the meaning of an already published field within the same version.

## Contract maintenance

Canonical foundation schemas:

- `contracts/http/common/api-error.v1.schema.json`
- `contracts/events/event-envelope.v1.schema.json`

Published feature contracts:

- `contracts/http/trip-search.v1.openapi.json`: `GET /api/v1/trips` owned by Catalog; Gateway exposes it as `GET /api/trips`.
- `contracts/http/trip-search.v1.example.json`: successful response fixture including fare version and timestamped availability.
- Catalog's `data_version` is a monotonic, Catalog-owned search-data version. The cache key includes it; relevant schedule/fare/station-route writes bump it transactionally, preventing an old entry from being represented as current.
- `contracts/http/seat-map.v1.openapi.json`: `GET /api/v1/trips/{tripId}/seats`, owned by Catalog; Gateway exposes it as `GET /api/trips/{tripId}/seats`.
- Seat-map Redis entries are scoped by trip, journey, and Catalog projection data version; a cached response recalculates `is_stale` when read. The version changes atomically with a projection event, so an older cached map is not represented as current.
- `contracts/http/identity-passengers.v1.openapi.json`: authenticated Cognito identity and customer-owned passenger-profile operations, owned by Catalog; Gateway exposes the public `/api` equivalents.
- `contracts/http/holds.v1.openapi.json`: Booking Engine hold submission and result polling. `POST /api/v1/holds` requires the idempotency key and trusted user context and returns `202` only after the `HoldRequested.v1` command is durably acknowledged. The public Gateway aliases use `/api` paths.
- `contracts/http/orders.v1.openapi.json`: Order & Payment creates an idempotent `PENDING_PAYMENT` order from an active owned hold. It stores immutable passenger, fare, trip, and seat snapshots and emits `OrderCreated.v1` through its transactional outbox.
- `contracts/events/hold-requested.v1.schema.json`: the durable command record on `booking.hold-commands.v1`. Commands contain one to four segment claims, use `trip_id:carriage_id` mapping version `v1`, and a multi-carriage command is coordinated by the deterministic first sorted shard key.
- A `HELD` booking-request result always carries one logical `hold_id`: it is a physical hold for single-shard work or a saga identity for multi-carriage work. The Booking Engine resolves either identity through `GET /api/holds/{holdId}`; physical reservation and allocation foreign keys remain database-enforced.
- Booking shard snapshots are internal Redis records keyed by mapping version and Kafka partition. They contain a version, source offset, owner epoch, and occupied-key set; they accelerate recovery only and must be discarded in favor of Kafka replay when invalid or unavailable.
- `contracts/events/booking-seat-state.v1.schema.json`: Booking Engine `SeatHeld.v1`, `HoldExpired.v1`, `HoldCancelled.v1`, and `HoldConfirmed.v1` inputs for the Catalog projection. A cancellation releases a provisional hold without being recorded as TTL expiry. They use topic `booking.seat-state.v1`, key `trip_id:seat_id`, and the Catalog injects Kafka partition offset as `source_position`; events are deduplicated by `event_id` and guarded by a per-seat-segment cursor.
- Booking emits these events from its PostgreSQL transactional outbox only after the corresponding hold lifecycle transaction commits. The local Compose `kafka-init` job provisions the source topic and Catalog DLQ with seven-day retention.

- When OpenAPI, AsyncAPI, or a schema registry is added, record its canonical path here.
- Every contract change must update its producer, consumers, contract tests, and relevant feature plan.

### Simulator payment lifecycle

- [Payment simulator HTTP v1](../../contracts/http/payments.v1.openapi.json) defines the owner-authenticated simulator route and exact-body HMAC-SHA256 webhook. Each order has one simulator attempt. The callback transaction ID and its first accepted outcome are durable.
- [Payment lifecycle events v1](../../contracts/events/payment-lifecycle.v1.schema.json) use topic `ordering.payments.v1` with key `order_id`. Booking confirms the complete logical hold and persists one result per order and transaction. Order & Payment moves to `PAID` only when it consumes a matching `BookingConfirmed.v1`.
- `PAYMENT_REVIEW` records that simulated compensation is required. Late success and rejected allocation produce review events for reconciliation. See [feature 008](../features/008-payment-simulator-and-idempotent-webhooks.md).

### Electronic tickets

- [Ticket HTTP v1](../../contracts/http/tickets.v1.openapi.json) defines owner-only discovery and PDF access, and public boolean validation of opaque QR references.
- Ticketing consumes `PaymentSucceeded.v1` and requests `GET /internal/v1/paid-orders/{id}/items` over the private service network. The response has `ownerId` and `items` with `itemId`, `tripId`, `seatId` and `segmentIndex`. An unpaid or missing order returns 404.
- [TicketIssued.v1](../../contracts/events/ticket-issued.v1.schema.json) uses topic `ticketing.events.v1` keyed by ticket ID. Issuance, inbox, and delivery jobs commit together. See [feature 010](../features/010-electronic-ticket-issuance-and-delivery.md).

### Edge traffic and operations

- [Feature 011](../features/011-peak-traffic-control-and-waiting-room.md) implements `POST /api/v1/waiting-room/admission`. An authenticated subject receives a queue position or an admission token. Present the token in `X-Admission-Token` on `POST /api/holds`. A grant is valid for one hold submission.
- [Feature 012](../features/012-administration-reporting-and-operations.md) adds Catalog-owned management and audit via `/api/v1/admin/catalog/{resource}`. The operator overview at `/api/v1/admin/reports/overview` reads the Ticketing reporting projection and includes the responding Gateway instance's pressure and Redis queue depth when enabled.

The admin overview combines Ticketing's asynchronous reporting read model with the private Booking pressure sample. `booking.logicalActiveHolds` counts active standalone holds and logical multi-carriage holds once each, capped at 10,001. The Booking object can be null when its sample is unavailable. See [Admin HTTP v1](../../contracts/http/admin.v1.openapi.json).
