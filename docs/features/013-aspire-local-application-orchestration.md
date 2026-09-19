# 013 — Aspire local application orchestration

Status: `in-progress`
Owner: `Codex`
Depends on: `002`

## Outcome

Developers can launch TetRail's application processes through a .NET Aspire AppHost and inspect their state, logs, and health from the Aspire dashboard while Docker Compose continues to own the existing local stateful dependencies.

## Scope

### In scope

- An Aspire AppHost that starts the four .NET services and the Go Booking Engine API.
- Project references and service discovery wiring from Gateway to Catalog.
- Developer instructions for starting Compose dependencies and the AppHost.

### Out of scope

- Replacing Docker Compose, changing its images, volumes, or data lifecycle.
- Adding new business behavior, contracts, migrations, or production deployment topology.
- Modeling Kafka or MinIO as Aspire resources before a service consumes them.

## Acceptance criteria

- [x] The solution contains a buildable .NET Aspire AppHost targeting the repository's .NET 9 baseline.
- [x] The AppHost launches Gateway, Catalog, Order & Payment, Ticketing, and the Booking Engine API with named endpoints.
- [x] Gateway resolves Catalog through Aspire service discovery when run by the AppHost and retains its existing standalone configuration.
- [x] Documentation states that Compose remains responsible for PostgreSQL, Redis, Kafka, and MinIO, and provides the AppHost launch command.
- [ ] The AppHost exposes the Gateway HTTPS endpoint at `https://localhost:54757` on every local launch.

## Design and impact

- Service/module: local orchestration AppHost; Gateway is changed only to use service discovery for its existing Catalog HTTP call.
- API/event contract: none.
- Data/migration: none; Compose volumes remain unchanged.
- Security/observability: Aspire dashboard is development-only; the AppHost does not define or expose production infrastructure.
- Key decisions: use Aspire for process orchestration and local inspection while retaining Compose as the source of truth for the local stateful platform.

## Implementation

- [x] Add AppHost project and register application processes.
- [x] Enable service discovery in Gateway and wire its Catalog dependency.
- [x] Add solution and local-development documentation updates.
- [x] Pin the Gateway AppHost HTTPS endpoint to port `54757` and retain its Swagger dashboard link.

## Verification

- [x] Build: `dotnet build TetRail.slnx` completed with 0 warnings and 0 errors.
- [x] Regression tests: `dotnet test TetRail.slnx --no-build` passed (8/8).
- [x] AppHost: `dotnet run --no-build --project infrastructure/TetRail.AppHost/TetRail.AppHost.csproj --launch-profile Aspire` started the dashboard and all five application resources.

## Progress notes

- 2026-09-18: Compose remains the local owner of PostgreSQL, Redis, Kafka, and MinIO; Aspire is limited to application-process orchestration.
- 2026-09-18: Solution build completed with 0 warnings/errors; 8/8 .NET tests passed. The AppHost started the dashboard and Gateway, Catalog, Order & Payment, Ticketing, and Booking Engine resources. Docker Compose inspection was not run because this sandbox cannot access the Docker daemon pipe.
- 2026-09-19: Pinned the Gateway AppHost HTTPS endpoint and its Swagger dashboard URL to `https://localhost:54757`; runtime verification is pending.

## Remaining risks

- Docker Compose runtime health remains an external prerequisite for Catalog's PostgreSQL/Redis-backed endpoint; it was not revalidated in this sandbox because the Docker daemon pipe is permission-restricted.
