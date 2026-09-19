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
- `contracts/http/seat-map.v1.openapi.json`: `GET /api/v1/trips/{tripId}/seats`, owned by Catalog; Gateway exposes it as `GET /api/trips/{tripId}/seats`.
- `contracts/events/booking-seat-state.v1.schema.json`: Booking Engine `SeatHeld.v1`, `HoldExpired.v1`, and `HoldConfirmed.v1` inputs for the Catalog projection; events are deduplicated by `event_id` and ordered by `source_position` per seat segment.

- When OpenAPI, AsyncAPI, or a schema registry is added, record its canonical path here.
- Every contract change must update its producer, consumers, contract tests, and relevant feature plan.
