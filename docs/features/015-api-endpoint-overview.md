# 015 — API endpoint overview

Status: `done`
Owner: `unassigned`
Depends on: `003-trip-search-and-fare-discovery`

## Outcome

Provide one concise, source-backed reference of TetRail HTTP endpoints across all five core services.

## Scope

### In scope

- Document implemented health and trip-search routes.
- List architecture-approved, not-yet-implemented public contracts separately.
- Identify the owning service and public gateway route for each endpoint.

### Out of scope

- Implementing missing APIs or changing HTTP contracts.
- Replacing the canonical OpenAPI document for trip search.

## Acceptance criteria

- [x] A single API overview lets a reader locate every implemented endpoint by service.
- [x] Planned contracts are visibly distinguished from implemented endpoints.
- [x] The overview links to the canonical trip-search OpenAPI schema.

## Design and impact

- Service/module: documentation across all five core services.
- API/event contract: no contract change; documents existing and architecture-approved contracts.
- Data/migration: none.
- Security/observability: records correlation and idempotency requirements where established.
- Key decisions: implemented and planned routes must never be presented as equivalent.

## Implementation

- [x] Inspect endpoint registrations and existing contract documents.
- [x] Add the consolidated API overview.
- [x] Update related documentation/contracts.

## Verification

- [x] Documentation review: endpoint registrations in `services/` match the implemented inventory.
- [x] Contract review: trip-search route matches `contracts/http/trip-search.v1.openapi.json`.
- [x] Performance/race/security: N/A — documentation-only change.

## Progress notes

- 2026-09-19: Created `docs/api-overview.md`; live and planned endpoints are separated to prevent an incorrect integration assumption.

## Remaining risks

- The document requires an update whenever a service registers, removes, or changes an HTTP endpoint.
