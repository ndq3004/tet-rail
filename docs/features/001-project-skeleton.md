# 001 — Project skeleton

Status: `done`  
Owner: `Codex`  
Depends on: `000`

## Outcome

The monorepo contains buildable skeletons for five core services, a frontend, shared contracts, local infrastructure, and generated-file ignore conventions.

## Scope

### In scope

- Four minimal ASP.NET Core services with health endpoints.
- A minimal Go Booking Engine with a health endpoint and unit test.
- A minimal React/TypeScript frontend.
- Solution/build conventions and placeholder contract, test, and infrastructure directories.
- A root `.gitignore` and README covering repository layout and local commands.

### Out of scope

- Domain implementation, database migrations, Kafka/Redis integration, and authentication.
- Runtime/dependency installation or complete local-infrastructure startup.
- Production Docker/Kubernetes manifests.

## Acceptance criteria

- [x] All five core services exist with the agreed runtime and boundaries.
- [x] Four .NET services build in one solution and expose `/health`.
- [x] The Go Booking Engine has an entry point, `/health`, and a handler test.
- [x] The frontend has React + TypeScript configuration and a placeholder screen.
- [x] Canonical locations exist for HTTP/event contracts, tests, and infrastructure.
- [x] `.gitignore` covers .NET, Go, Node, IDE, environment, and build artifacts.
- [x] README documents the repository tree and verification commands.

## Design and impact

- Service/module: entire monorepo foundation.
- API/event contract: placeholders only; no schema published yet.
- Data/migration: none.
- Security/observability: no secrets; health endpoints do not expose dependency details.
- Key decisions: deployables do not share domain assemblies; common .NET build settings live at the root.

## Implementation

- [x] Create the solution and four .NET web projects.
- [x] Create the Go Booking Engine module/test.
- [x] Create the React frontend skeleton.
- [x] Create contract, infrastructure, and test placeholders.
- [x] Add `.gitignore` and the repository README.
- [x] Update the relevant knowledge/index files.

## Verification

- [x] .NET: `dotnet build TetRail.slnx` — passed, 0 warnings/errors.
- [x] Go: `go test ./...` — NOT RUN because Go was not installed; entry point, handler, and test files were structurally checked.
- [x] Frontend: `npm install` and `npm run build` — passed, 0 vulnerabilities.
- [x] Structure: required files, five `/health` handlers, and ignore patterns are present.

## Progress notes

- 2026-09-05: .NET SDK 9.0.317 and Node 24.14.1 were available; Go was not on PATH.
- 2026-09-05: .NET solution and frontend production builds passed; structural checks passed.

## Remaining risks

- Go compilation/tests must run in CI or an environment with Go 1.24+.
