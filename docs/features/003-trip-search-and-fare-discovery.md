# 003 — Trip search and fare discovery

Status: `done`
Owner: `Codex`
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

## Services to modify

| Service directory | Modification | Responsibility in this feature |
|---|---|---|
| `services/catalog` | **Required — primary service** | Own the station, route, trip, seat-class, and fare data; implement trip-search query logic, PostgreSQL persistence, Redis cache-aside, validation, telemetry, and the search endpoint. |
| `services/gateway` | **Required — integration only** | Expose and proxy the versioned trip-search route, preserve correlation/trace context, and apply the existing public-read timeout policy. It must not contain catalog domain or fare logic. |
| `services/booking-engine` | **No modification** | Authoritative seat allocation and holds belong to features 004/005; this feature consumes only best-effort availability metadata. |
| `services/order-payment` | **No modification** | Order creation and fare snapshots begin in feature 007. |
| `services/ticketing` | **No modification** | Ticketing, notification, and reporting are outside this feature. |

Shared, non-service changes are expected under `contracts/` for OpenAPI v1 and under `tests/` for contract/integration coverage. Frontend changes are not part of this feature plan.

## Acceptance criteria

- [x] Search requires valid origin, destination, and date; origin differs from destination and occurs earlier on the trip route.
- [x] Results contain only trips serving the requested segment/date, sorted by departure time, with trip ID, stations, departure/arrival times, duration, and schedule status.
- [x] Each result lists sellable seat classes, price, currency, `fare_version`, and `effective_at`; an out-of-range fare is never returned.
- [x] Each result has approximate availability and `availability_as_of`; the contract states that this projection may be stale and does not guarantee hold success.
- [x] Cache keys isolate origin, destination, date, and data version; schedule/fare changes cannot present old data as current.
- [x] A cache miss reads PostgreSQL and warms the cache; Redis failure does not return incorrect data or convert dependency failure into “sold out.”
- [x] A stale projection is clearly marked with a timestamp; when data is insufficient for a correct response, the API returns API error v1 instead of guessing.
- [x] Requests carry or receive `correlation_id`; logs contain no sensitive data, and metrics cover latency, errors, cache hit/miss/stale/fallback.
- [x] OpenAPI v1 and contract tests lock request, response, validation errors, and projection timestamp/version semantics.

## Design and impact

- Service/module: primary changes in `services/catalog`; integration-only routing changes in `services/gateway`; no changes to the other three core services. Frontend integration may be planned separately.
- API/event contract: publish OpenAPI v1 for `GET /api/trips`; use `api-error.v1`; no domain event in this feature.
- Data/migration: add Catalog/Schedule-owned schema for stations, route stops, trips, carriage/seat classes, and fare effective ranges; migrations require rollback before production data or a documented forward-fix afterward.
- Security/observability: read endpoint is public through the Gateway under MVP policy; inputs are bounded; correlation/trace context propagates; station/date must not create high-cardinality metric labels.
- Key decisions: Catalog/Schedule owns schedules and fares; Redis is only a cache; availability is a timestamped best-effort projection and cannot bypass Booking Engine atomic checks.
- Cache-version decision: a Catalog-owned monotonic search-data version is bumped transactionally by schedule/fare/station-route changes. Search reads that small version record before Redis, so an old search entry is never treated as current after a relevant change.

## Implementation

- [x] Finalize the station, ordered route-stop, trip schedule, seat-class, and effective-dated fare models; add migrations and seed data.
- [x] Define OpenAPI v1, examples, and validation/error mapping for trip search.
- [x] Implement segment/date querying and map duration, fare version/effective time, and availability metadata.
- [x] Add cache-aside, versioned keys, TTL/invalidation hooks, and Redis-failure fallback.
- [x] Add correlation/tracing, structured logs, and search-path metrics.
- [x] Add unit, integration, and contract tests for happy path, route ordering, fare effective range, cache miss/stale, and Redis failure.
- [x] Update related documentation/contracts and knowledge.

## Verification

- [x] Unit/component: `dotnet test tests/catalog/TetRail.Catalog.Tests.csproj --no-restore` passed 21 tests, including cache-version, stale-cache, validation, fallback, API-error, and OpenAPI contract coverage.
- [x] Integration/contract: Catalog migration runner applied `006_trip_search_data_version.sql`; live PostgreSQL/Redis checks proved cache miss/hit, cache-version invalidation, Redis fallback, and `503 CATALOG_UNAVAILABLE` for a missing projection. OpenAPI contract tests are included in the unit test command above.
- [x] Performance/race/security: warm-cache smoke completed 25 requests in 342 ms; input bounds and telemetry cardinality were reviewed. Race test is N/A for this .NET service without a shared-memory hot path.
- [x] Manual: seed-data checks covered a valid segment, invalid reverse segment, no-trip date, stale availability, cache-version update, Redis outage, and an unavailable projection; test data was restored.

## Progress notes

- 2026-09-18: Planned from US-02 and accepted architecture decisions; implementation has not started.
- 2026-09-18: Implementation started for catalog search, gateway routing, contracts, persistence/cache adapters, and tests.
- 2026-09-18: Catalog/gateway build passes and 8 unit/endpoint/contract tests pass. Live PostgreSQL/Redis verification remains pending because Docker Desktop is unavailable.
- 2026-09-21: Added transactional Catalog search-data versioning, stale-cache recalculation, unavailable-projection API error handling, and regression coverage. Catalog build and 21 tests pass; live PostgreSQL/Redis verification remains blocked because the Docker Desktop/Linux engine is not running.
- 2026-09-21: Docker Desktop became available. Applied Catalog migrations through the development endpoint and completed live PostgreSQL/Redis plus manual verification. Catalog build passed with zero warnings/errors; 21/21 tests passed.

## Remaining risks

- The source and update mechanism for the availability summary will be completed with feature 004; this feature defines only a best-effort projection contract and never treats it as the seat-sale authority.
