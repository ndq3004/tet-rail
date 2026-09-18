# 003 — Trip search and fare discovery

Status: `planned`  
Owner: `unassigned`  
Depends on: `002`

## Outcome

Customers can search by origin station, destination station, and departure date, then view schedules, duration, seat classes, effective fares, and approximate availability before opening a seat map.

## Scope

### In scope

- `GET /api/trips?from=&to=&date=` in the Identity, Catalog & Schedule service.
- Minimum data model for stations, routes/route stops, trips, carriage/seat classes, and versioned/effective-dated fares.
- Development seed data covering a multi-stop route and multiple seat classes.
- Redis cache-aside for search results; keys include origin, destination, date, and relevant data versions.
- Results include projection/cache generation time and approximate availability, which never confirms that a seat can be held.
- Controlled degradation on cache miss, Redis failure, or stale projection, with distinct cache hit/miss/stale/fallback telemetry.
- OpenAPI v1, API error v1, and request/response contract tests.

### Out of scope

- Per-seat maps and states; feature 004.
- Holds or authoritative availability decisions; feature 005.
- Administrative schedule/fare CRUD, authentication, waiting room, and high-load optimization.
- Inventory shards, booking commands, hold events, or Booking Engine changes.

## Acceptance criteria

- [ ] Search requires valid origin, destination, and date; origin differs from destination and occurs earlier on the trip route.
- [ ] Results contain only trips serving the requested segment/date, sorted by departure time, with trip ID, stations, departure/arrival times, duration, and schedule status.
- [ ] Each result lists sellable seat classes, price, currency, `fare_version`, and `effective_at`; an out-of-range fare is never returned.
- [ ] Each result has approximate availability and `availability_as_of`; the contract states that this projection may be stale and does not guarantee hold success.
- [ ] Cache keys isolate origin, destination, date, and data version; schedule/fare changes cannot present old data as current.
- [ ] A cache miss reads PostgreSQL and warms the cache; Redis failure does not return incorrect data or convert dependency failure into “sold out.”
- [ ] A stale projection is clearly marked with a timestamp; when data is insufficient for a correct response, the API returns API error v1 instead of guessing.
- [ ] Requests carry or receive `correlation_id`; logs contain no sensitive data, and metrics cover latency, errors, cache hit/miss/stale/fallback.
- [ ] OpenAPI v1 and contract tests lock request, response, validation errors, and projection timestamp/version semantics.

## Design and impact

- Service/module: Identity, Catalog & Schedule; frontend integration may be planned separately.
- API/event contract: publish OpenAPI v1 for `GET /api/trips`; use `api-error.v1`; no domain event in this feature.
- Data/migration: add Catalog/Schedule-owned schema for stations, route stops, trips, carriage/seat classes, and fare effective ranges; migrations require rollback before production data or a documented forward-fix afterward.
- Security/observability: read endpoint is public through the Gateway under MVP policy; inputs are bounded; correlation/trace context propagates; station/date must not create high-cardinality metric labels.
- Key decisions: Catalog/Schedule owns schedules and fares; Redis is only a cache; availability is a timestamped best-effort projection and cannot bypass Booking Engine atomic checks.

## Implementation

- [ ] Finalize the station, ordered route-stop, trip schedule, seat-class, and effective-dated fare models; add migrations and seed data.
- [ ] Define OpenAPI v1, examples, and validation/error mapping for trip search.
- [ ] Implement segment/date querying and map duration, fare version/effective time, and availability metadata.
- [ ] Add cache-aside, versioned keys, TTL/invalidation hooks, and Redis-failure fallback.
- [ ] Add correlation/tracing, structured logs, and search-path metrics.
- [ ] Add unit, integration, and contract tests for happy path, route ordering, fare effective range, cache miss/stale, and Redis failure.
- [ ] Update related documentation/contracts and knowledge.

## Verification

- [ ] Unit/component: run Identity, Catalog & Schedule tests for validation, route-segment matching, duration, and effective-fare selection.
- [ ] Integration/contract: run PostgreSQL/Redis integration tests and OpenAPI contract tests; cover cache miss, cache hit, stale projection, and Redis unavailable.
- [ ] Performance/race/security: run read-path smoke/load tests with warm/cold cache and inspect input bounds and telemetry cardinality; race test is N/A for this .NET service without a shared-memory hot path.
- [ ] Manual: with seed data, search a valid segment, an invalid reverse segment, a date with no trips, and fare effective-time boundaries.

## Progress notes

- 2026-09-18: Planned from US-02 and accepted architecture decisions; implementation has not started.

## Remaining risks

- The source and update mechanism for the availability summary will be completed with feature 004; this feature defines only a best-effort projection contract and never treats it as the seat-sale authority.
