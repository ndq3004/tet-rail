# 017 — Catalog migration runner

Status: `in-progress`  
Owner: `Codex`  
Depends on: `002`

## Outcome

Catalog can apply its owned SQL migrations through a direct development-only operational endpoint without exposing that operation through Gateway.

## Scope

### In scope

- A Catalog-only endpoint that applies embedded `Migrations/*.sql` files in lexical order.
- A Catalog-owned migration history table, transactional application, and PostgreSQL advisory lock.
- Component tests for the endpoint registration and runner result handling.

### Out of scope

- Gateway routing, public API documentation, or cross-service migrations.
- Automatic migrations at application startup or production deployment orchestration.

## Acceptance criteria

- [x] `POST /internal/migrations/run` is available directly from Catalog only in Development and is not registered by Gateway.
- [x] Each migration is recorded after it succeeds; repeated calls skip recorded migrations.
- [ ] Concurrent runs serialize, and a failed migration is not recorded.
- [x] The runner executes only Catalog-owned embedded SQL files in deterministic order.

## Design and impact

- Service/module: `services/catalog`; no Gateway change.
- API/event contract: unversioned, development-only operational endpoint; it is intentionally excluded from public contracts.
- Data/migration: creates `catalog.schema_migrations`; migrations run one transaction each and use a PostgreSQL transaction advisory lock. A failed migration is a forward-fix requirement.
- Security/observability: endpoint is registered only in Development, returns no connection details, and logs only migration names and correlation IDs.
- Key decisions: migration execution remains explicit (`POST`) rather than startup behavior; SQL files are embedded at build time so deployment working directories cannot alter what runs.

## Implementation

- [x] Define the endpoint, migration history behavior, and execution safety rules.
- [x] Implement the runner and Development-only Catalog endpoint.
- [x] Add component tests and update the API inventory.

## Verification

- [x] Unit/component: `dotnet test tests/catalog/TetRail.Catalog.Tests.csproj` — 13/13 passed using an isolated output directory because the default Catalog executable is in use.
- [ ] Integration/contract: NOT RUN — requires a PostgreSQL instance to prove transactional execution and advisory-lock behavior.
- [x] Performance/race/security: manual route inspection confirms Gateway has no migration route.

## Progress notes

- 2026-09-19: Plan created for an explicit Catalog-local migration action; no startup or Gateway exposure is permitted.
- 2026-09-19: Added embedded SQL execution, per-migration transactions, a transaction advisory lock, and `catalog.schema_migrations`. Component tests passed (13/13); live PostgreSQL execution could not run because Docker daemon access is denied in this environment.

## Remaining risks

- PostgreSQL-backed concurrent and failed-migration behavior remains to be verified when the local platform is available.
