# 016 — Local Swagger UI

Status: `done`
Owner: `unassigned`
Depends on: `013-aspire-local-application-orchestration`

## Outcome

Developers can open an interactive, generated API reference for each .NET service directly from the Aspire dashboard during local development.

## Scope

### In scope

- Generate OpenAPI documents and serve Swagger UI at `/swagger` in development for Gateway, Catalog, Order & Payment, and Ticketing.
- Add Swagger UI deep links for those services to the Aspire dashboard.
- Document local access and the Booking Engine limitation.

### Out of scope

- Exposing API documentation in production.
- Publishing schemas for endpoints that are not implemented.
- Adding Swagger UI to the Go Booking Engine before it has an OpenAPI document.

## Acceptance criteria

- [x] Each .NET service exposes `/openapi/v1.json` and `/swagger` in the Development environment.
- [x] Aspire shows a Swagger UI link for each .NET service.
- [x] The documented route inventory only includes implemented endpoints.

## Design and impact

- Service/module: four .NET services and the local Aspire AppHost.
- API/event contract: generated local documentation only; no API behavior changes.
- Data/migration: none.
- Security/observability: OpenAPI documents and Swagger UI are restricted to the Development environment.
- Key decisions: use ASP.NET Core's built-in OpenAPI generation with Swagger UI assets; no local UI is added for the Go service until it publishes an OpenAPI document.

## Implementation

- [x] Add local OpenAPI and Swagger UI support to the .NET services.
- [x] Add Aspire dashboard Swagger deep links.
- [x] Update local development and contract documentation.

## Verification

- [x] Build: `dotnet build TetRail.slnx` completed with 0 warnings and 0 errors.
- [x] Tests: `dotnet test TetRail.slnx --no-build` passed (8/8).
- [x] Endpoint check: Order & Payment in Development returned `200` from `/swagger` and `/openapi/v1.json`; the generated document includes `/health`.

## Progress notes

- 2026-09-19: Scope finalized; Swagger UI will only list endpoints registered by the running service.
- 2026-09-19: Build and tests succeeded. The Aspire AppHost compiles with a `/swagger` deep link for each .NET service endpoint; an Order & Payment runtime check confirmed the development-only UI and document are reachable.

## Remaining risks

- None.
