# 002 — Local platform foundation

Status: `done`  
Owner: `Codex`  
Depends on: `001`

## Outcome

Developers can start and inspect foundational local dependencies, use common HTTP-error and event-metadata contracts, and run Booking Engine tests even when Go is not installed locally.

## Scope

### In scope

- Docker Compose for PostgreSQL, Redis, Kafka, and MinIO with health checks.
- Example local configuration without real secrets.
- JSON Schema v1 for API errors and event envelopes.
- A way to run Go tests with a local or version-pinned container toolchain.

### Out of scope

- Service integration, business migrations/schemas, seed data, observability, CI, and production/HA.

## Acceptance criteria

- [x] Compose validates and defines all four dependencies with health checks.
- [x] Local values are overridable; the repository contains development-only placeholders.
- [x] API error and event envelope schemas are versioned, include correlation metadata, and have valid examples.
- [x] Booking Engine tests run with local Go 1.24 or a container.
- [x] README documents commands to start, inspect, stop, and verify the stack.

## Design and impact

- Service/module: local infrastructure, language-neutral contracts, and Booking Engine workflow.
- API/event contract: adds API error v1 and event envelope v1; no producers or consumers yet.
- Data/migration: local named volumes only; data reset is manual.
- Security/observability: default credentials are local-only and overridable.
- Key decisions: local Kafka uses single-node KRaft; Go 1.24 follows `go.mod`; integration is deferred to the first consuming feature.

## Implementation

- [x] Add Compose, environment example, and instructions.
- [x] Add common contract schemas and examples.
- [x] Update documentation/knowledge.

## Verification

- [x] Config: `docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml config --quiet`.
- [x] Contracts: PowerShell `Test-Json` validates two schemas and two examples.
- [x] Build/tests: .NET build, frontend build, and Booking Engine `go test ./...` passed.
- [x] Runtime health: Compose `up -d --wait` and 4/4 containers healthy.

## Progress notes

- 2026-09-06: Docker CLI/Compose was available, but Docker Desktop was not running; Go was not on PATH.
- 2026-09-06: Compose config passed with 4/4 services defining health checks; 2/2 contract examples were valid; .NET built with 0 warnings/errors; frontend production build passed.
- 2026-09-06: Docker Desktop was started, but `docker desktop status` reported `stopped` and the Linux engine named pipe did not exist.
- 2026-09-06: After Docker started successfully, Go tests passed and PostgreSQL, Redis, Kafka, and MinIO were 4/4 healthy. Final verification: .NET 0 warnings/errors, frontend production build passed, 2/2 contract examples valid.

## Remaining risks

- none.
