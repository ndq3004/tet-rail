# TetRail API Overview

Status: current implementation and approved contract inventory
Last reviewed: 2026-09-19

This is the quick reference for HTTP APIs across TetRail's five core services. It is an inventory, not a replacement for versioned schemas. **Implemented** endpoints are registered in the current service source. **Planned** endpoints are approved architecture/contracts that are not registered yet.

## At a glance

| Service | Runtime | Implemented endpoints | Planned business endpoints |
|---|---|---:|---:|
| Gateway & Traffic Control | .NET | 2 | 0 |
| Identity, Catalog & Schedule | .NET | 2 | 1 |
| Booking Engine | Go | 1 | 3 |
| Order & Payment | .NET | 1 | 4 |
| Ticket, Notification & Reporting | .NET | 1 | 1 |

All service health checks return `200 OK` with `{ "service": "...", "status": "healthy" }`.

## Implemented endpoints

| Service | Method and path | Purpose | Responses / notes |
|---|---|---|---|
| Gateway | `GET /health` | Gateway health check. | `200` |
| Gateway | `GET /api/trips?from=&to=&date=` | Public trip-search route; proxies to Catalog. | Passes through Catalog responses. Returns `503 CATALOG_UNAVAILABLE` if Catalog cannot be reached. Accepts or generates `X-Correlation-ID`. |
| Catalog | `GET /health` | Catalog health check. | `200` |
| Catalog | `GET /api/v1/trips?from=&to=&date=` | Search scheduled trips, effective fares, and timestamped availability. | `200`, `400 VALIDATION_FAILED`, `503 CATALOG_UNAVAILABLE`. Canonical schema: [trip-search.v1.openapi.json](../contracts/http/trip-search.v1.openapi.json). |
| Booking Engine | `GET /health` | Booking Engine health check. | `200` |
| Order & Payment | `GET /health` | Order & Payment health check. | `200` |
| Ticket, Notification & Reporting | `GET /health` | Ticketing health check. | `200` |

### Trip-search notes

- The public client route is Gateway's `GET /api/trips`; Catalog owns the versioned internal contract at `GET /api/v1/trips`.
- `from`, `to`, and `date` are required by the Catalog contract. `date` uses ISO date format (`YYYY-MM-DD`).
- Availability is a timestamped projection; it is informative only and does not guarantee a hold will succeed.
- The `X-Correlation-ID` header is propagated by Gateway and Catalog; clients may supply a valid UUID.

## Planned endpoints

These routes are documented in the approved architecture and contract knowledge, but they are **not live**. Do not integrate against them until their versioned schemas and service implementations are published.

| Owner | Method and path | Purpose | Contract requirements |
|---|---|---|---|
| Catalog | `GET /api/trips/{tripId}/seats` | Read the timestamped seat-availability projection. | Projection can be stale; it cannot allocate a seat. |
| Booking Engine | `POST /api/holds` | Request an expiring, atomic seat hold. | Requires `Idempotency-Key`; returns `202 Accepted` and `request_id` only after durable append. |
| Booking Engine | `GET /api/booking-requests/{requestId}` | Poll asynchronous hold processing. | Returns a result such as `HELD` or `REJECTED`; only `HELD` with `hold_id` and `expires_at` confirms a hold. |
| Booking Engine | `GET /api/holds/{holdId}` | Read an existing hold. | Includes expiry time; a hold is normally valid for 10 minutes. |
| Order & Payment | `POST /api/orders` | Create an order from an active hold. | Rejects expired or foreign holds; snapshots fares and passenger data. |
| Order & Payment | `POST /api/payments/{orderId}/simulate` | Start a simulator payment scenario. | Supports success, failure, timeout, delayed success, and duplicate callback scenarios. |
| Order & Payment | `POST /api/payment-webhooks/simulator` | Receive payment-simulator callback. | Signature authentication and provider-transaction deduplication are required. |
| Order & Payment | `GET /api/orders/{orderId}` | Read order and payment status. | Includes lifecycle state such as `PENDING_PAYMENT`, `PAID`, or `PAYMENT_REVIEW`. |
| Ticketing | `GET /api/tickets/{ticketId}` | Read authorized ticket metadata or resources. | Ticket issuance occurs after booking confirmation. |

## Shared conventions

| Concern | Convention |
|---|---|
| Versioning | Version externally shared HTTP/event contracts. A breaking change needs a new version or a compatibility plan. |
| Errors | Use the shared `api-error.v1` schema for published error contracts. |
| Correlation | Carry a correlation ID through requests and events; do not log secrets, tokens, full identifiers, or sensitive payment data. |
| Idempotency | Commands and retryable webhooks are idempotent. Hold requests use `Idempotency-Key`; payment callbacks deduplicate by provider transaction ID. |
| Booking acknowledgement | `202 Accepted` means the booking command was durably recorded, not that inventory was allocated. |

## Source of truth and maintenance

- Implemented routes: service endpoint registrations under `services/`.
- Published trip-search schema: [trip-search.v1.openapi.json](../contracts/http/trip-search.v1.openapi.json).
- Approved future HTTP inventory: [contract knowledge](knowledge/contracts.md), [architecture](architecture.md), and [product brief](product-brief.md).
- When an endpoint changes, update this overview, its versioned schema, relevant tests, and the active feature plan together.
