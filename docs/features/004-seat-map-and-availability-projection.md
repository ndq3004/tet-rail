# 004 — Seat map and availability projection

Status: `in-progress`
Owner: `Codex`
Depends on: `003`

## Outcome

Customers can retrieve a timestamped, segment-aware seat map for a selected trip and journey. Each seat shows its carriage, class/type, and projected `AVAILABLE`, `HELD`, or `SOLD` state, while the response makes clear that only a later hold request can confirm availability.

## Scope

### In scope

- Catalog-owned, versioned seat-map read API: `GET /api/v1/trips/{tripId}/seats?from=&to=`; Gateway exposes the public equivalent at `GET /api/trips/{tripId}/seats`.
- A Catalog/Schedule read model for carriage, seat, and segment-specific projected state, populated from idempotently consumed Booking Engine state events.
- Versioned event schemas for the projection inputs: `SeatHeld.v1`, `HoldExpired.v1`, `HoldConfirmed.v1`, and the corresponding rejection/expiry-safe transitions needed to remove a hold.
- A projection timestamp, monotonically increasing projection version or source offset, and clear stale/degraded response semantics.
- Redis cache-aside for rendered seat maps, with cache invalidation or versioned keys when projection state changes.
- OpenAPI/schema examples and tests for the HTTP and event contracts.

### Out of scope

- Hold creation, inventory-shard routing, booking command durability, or final segment allocation; feature 005 owns these.
- Order/payment behavior, ticket issuance, administration UI, and frontend seat-picker UX.
- Treating a read-model result, cache value, or distributed lock as proof that a seat can be sold.

## Acceptance criteria

- [ ] A valid trip, origin, and destination return every configured seat with stable seat/carriage identifiers, seat class/type, and one of `AVAILABLE`, `HELD`, or `SOLD`.
- [ ] The requested origin and destination are validated against the trip route, must differ, and must form an ordered journey interval; invalid input returns API error v1.
- [ ] Seat status is calculated over every occupied segment in `[from_stop_order, to_stop_order)`: any sold segment yields `SOLD`; otherwise any held segment yields `HELD`; otherwise the seat is `AVAILABLE`.
- [ ] The response includes `availability_as_of`, projection version/source position, and a stale indicator. It explicitly states that the projection is not a hold guarantee.
- [ ] Booking-state events are versioned, correlated, deduplicated, and applied idempotently; duplicate, reordered, expired, or superseded hold updates cannot make a confirmed segment appear available.
- [ ] Projection updates invalidate or bypass stale cache entries. Redis unavailability does not produce a false `AVAILABLE` result or prevent the authoritative Booking Engine from enforcing allocation.
- [ ] Gateway forwards the seat-map contract and correlation context without implementing Catalog or Booking domain logic.
- [ ] Contract, component, and integration tests cover state precedence, non-overlapping journeys, duplicate/reordered events, stale projection behavior, cache failure, and request validation.

## Design and impact

- Service/module: Catalog owns the seat-map projection, API, cache, and event consumer. Gateway only routes the public HTTP endpoint. Booking Engine remains unchanged in this feature; feature 005 becomes the producer of the defined booking events.
- API/event contract: adds versioned seat-map OpenAPI and examples plus versioned `SeatHeld`, `HoldExpired`, and `HoldConfirmed` schemas. The public response exposes projection metadata and never guarantees a successful hold.
- Data/migration: add Catalog-owned projection tables keyed by trip, seat, and segment, along with an inbox/source-position record for idempotent application. Roll back only before event consumption begins; afterward use forward migrations and replay/rebuild the projection from retained events.
- Security/observability: bound `tripId`, station identifiers, and response size; propagate `correlation_id`; avoid passenger, hold-owner, payment, or token data in the response/logs. Emit projection lag, event-apply success/failure, duplicate, stale-response, cache, and endpoint-latency metrics.
- Key decisions: Catalog owns read data only; Booking Engine is authoritative for holds and allocations. Status is journey-segment-aware and follows `SOLD` over `HELD` over `AVAILABLE`. Kafka events and the inbox make updates replayable and duplicate-safe; Redis is only an acceleration layer.

## Implementation

- [x] Finalize the seat-map HTTP contract, response examples, API error mapping, and booking-state event schemas.
- [x] Add Catalog-owned projection and inbox migrations plus a rebuild/replay procedure.
- [x] Implement idempotent event application, segment-aware status calculation, and stale/projection-version tracking.
- [x] Implement Catalog API, Redis cache-aside/invalidation, and Gateway forwarding with correlation propagation.
- [ ] Add unit, integration, event-consumer, cache-failure, and producer/consumer contract tests.
- [ ] Update contract knowledge and developer documentation with canonical schema locations and the projection/replay procedure.

## Verification

- [ ] Unit/component: `dotnet test tests/catalog/TetRail.Catalog.Tests.csproj` covers route validation, segment-state precedence, stale metadata, and cache-key/version behavior.
- [ ] Integration/contract: run PostgreSQL, Redis, and Kafka-backed projection tests; validate OpenAPI and event-schema examples; verify duplicate/reordered replay produces the same projection.
- [ ] Performance/race/security: run a seat-map read-path smoke/load check with warm/cold cache, inspect bounded response/query behavior and metric-label cardinality. Go race/benchmark checks are N/A because Booking Engine is not changed.
- [ ] Manual: query a multi-stop seeded trip for overlapping and non-overlapping journeys, then verify timestamp/version/stale metadata and public Gateway forwarding.

## Progress notes

- 2026-09-18: Planned from US-03, D-001 through D-006, and the Catalog/Schedule read-model boundary. Booking-state producers are intentionally deferred to feature 005; this plan defines their consumer-facing contracts and projection behavior.
- 2026-09-19: Implementation started. The Catalog read model, public contract, cache behavior, and Gateway forwarding are being added before Booking Engine producers are introduced in feature 005.
- 2026-09-19: Added the Catalog seat-map schema, seeded read-model seats, segment-aware query API, Redis cache-aside, and Gateway forwarding. Catalog unit tests pass (11/11) using an isolated build output because local Catalog/Gateway processes hold their default executables open.
- 2026-09-19: Registered the Aspire service-discovery provider in Gateway so its `https+http://catalog` reference resolves when launched by the AppHost; standalone runs retain the configured local Catalog URL.
- 2026-09-19: Registered the Gateway service-discovery resolver required by the Catalog client handler, preventing `No provider which supports the provided service name, 'https+http://catalog', has been configured` when the AppHost injects the Catalog endpoint.
- 2026-09-19: Added the idempotent Catalog projection-event applier and v1 HTTP/event contracts. Event application writes an inbox record and per-seat-segment cursor transactionally; a `SOLD` cursor cannot be downgraded by delayed hold/expiry events. Catalog tests pass (13/13) and contract JSON parses successfully. Kafka consumer wiring remains deferred until feature 005 owns the Booking Engine producer and topic configuration.

## Remaining risks

- The exact Kafka topic names, retention/replay window, and producer rollout sequencing must be finalized with feature 005 before live event consumption is enabled.
