# 018 — Local Kafka UI

Status: `done`  
Owner: `Codex`  
Depends on: `002`

## Outcome

Developers can inspect the local Kafka broker, topics, records, and consumer groups through a browser UI.

## Scope

### In scope

- Add a Kafka UI service to the local Docker Compose stack.
- Configure it to connect to the Compose Kafka service and expose a configurable local port.
- Document the local UI address.

### Out of scope

- Production Kafka administration, authentication, authorization, or topic-management policy.

## Acceptance criteria

- [x] Compose configuration defines a Kafka UI connected to the local Kafka broker.
- [x] The UI port is configurable through the local environment file.
- [x] Local infrastructure documentation provides the UI URL.

## Design and impact

- Service/module: local Docker Compose infrastructure only.
- API/event contract: none.
- Data/migration: none.
- Security/observability: the unauthenticated UI is local-development only and is not a production management surface.
- Key decisions: Kafka UI connects to the broker using the Compose service hostname, not the host-mapped listener.

## Implementation

- [x] Add the Kafka UI Compose service and environment setting.
- [x] Document access to the UI.

## Verification

- [x] Config: `docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml config --quiet` — passed.
- [ ] Runtime: NOT RUN to completion — the 184 MB Kafka UI image download did not complete within the available command window.

## Progress notes

- Kafka UI is intentionally a local-development tool.
- 2026-09-19: Compose config validation passed. The rendered configuration contains the `kafka-ui` service, `kafka:9092` bootstrap server, and the host port `8080`; `git diff --check` passed.

## Remaining risks

- The initial Kafka UI image pull is required before the browser UI can be opened.
