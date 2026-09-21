# 019 — Kafka UI Broker Connectivity

Status: `done`
Owner: `Codex`
Depends on: `018`

## Outcome

Kafka UI can load the local Kafka cluster while host-based applications continue to use the configurable local Kafka port.

## Scope

### In scope

- Configure separate Kafka listeners for Docker-network clients and host clients.
- Verify Kafka UI can obtain broker metadata and list topics through the Docker Compose stack.

### Out of scope

- Production Kafka access, authentication, authorization, or topic-management policy.

## Acceptance criteria

- [x] Kafka UI connects to `kafka:9092` after broker metadata resolution.
- [x] Host applications can connect through `localhost:${TETRAIL_KAFKA_PORT}`.
- [x] The rendered Compose configuration remains valid.

## Design and impact

- Service/module: local Docker Compose infrastructure only.
- API/event contract: none.
- Data/migration: none.
- Security/observability: Kafka UI remains unauthenticated and local-development only.
- Key decisions: advertise `kafka:9092` to Docker-network clients and `localhost` on a distinct container listener to host clients; a single advertised `localhost` endpoint is invalid for Kafka UI in a separate container.

## Implementation

- [x] Add internal and host Kafka listeners and map the host port to the host listener.
- [x] Verify the running Kafka UI and broker logs.

## Verification

- [x] Config: `docker compose -f infrastructure/compose/compose.yml config --quiet` — passed.
- [x] Runtime: `docker compose -f infrastructure/compose/compose.yml ps`, Kafka UI logs, and `GET /actuator/health` — passed; Kafka is healthy, the UI reported a metrics update for `tetrail-local`, and the health endpoint returned `{"status":"UP"}`.
- [x] Integration/contract: N/A — local infrastructure wiring only.

## Progress notes

- 2026-09-20: Diagnosed that Kafka advertises only `localhost:9092`; Kafka UI bootstraps through `kafka:9092` but receives unreachable `localhost` broker metadata.
- 2026-09-20: Recreated Kafka, Kafka UI, and topic initialization containers without deleting volumes. Kafka exposes its internal listener on `kafka:9092` and its host listener through the configurable `localhost` port.

## Remaining risks

- None.
