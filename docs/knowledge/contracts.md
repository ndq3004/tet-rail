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
- `contracts/events/hold-requested.v1.schema.json`: the durable command record on `booking.hold-commands.v1`. Commands contain one to four segment claims, use `trip_id:carriage_id` mapping version `v1`, and a multi-carriage command is coordinated by the deterministic first sorted shard key.
- A `HELD` booking-request result always carries one logical `hold_id`: it is a physical hold for single-shard work or a saga identity for multi-carriage work. The Booking Engine resolves either identity through `GET /api/holds/{holdId}`; physical reservation and allocation foreign keys remain database-enforced.
- Booking shard snapshots are internal Redis records keyed by mapping version and Kafka partition. They contain a version, source offset, owner epoch, and occupied-key set; they accelerate recovery only and must be discarded in favor of Kafka replay when invalid or unavailable.
- `contracts/events/booking-seat-state.v1.schema.json`: Booking Engine `SeatHeld.v1`, `HoldExpired.v1`, `HoldCancelled.v1`, and `HoldConfirmed.v1` inputs for the Catalog projection. A cancellation releases a provisional hold without being recorded as TTL expiry. They use topic `booking.seat-state.v1`, key `trip_id:seat_id`, and the Catalog injects Kafka partition offset as `source_position`; events are deduplicated by `event_id` and guarded by a per-seat-segment cursor.
- Booking emits these events from its PostgreSQL transactional outbox only after the corresponding hold lifecycle transaction commits. The local Compose `kafka-init` job provisions the source topic and Catalog DLQ with seven-day retention.

- When OpenAPI, AsyncAPI, or a schema registry is added, record its canonical path here.
- Every contract change must update its producer, consumers, contract tests, and relevant feature plan.
