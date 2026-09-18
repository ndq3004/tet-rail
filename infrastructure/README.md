# Infrastructure

Local orchestration, telemetry and deployment assets belong here.

- `compose/`: local PostgreSQL, Kafka, Redis, MinIO and observability stack.
- `observability/`: dashboards, collectors and alert rules.

Infrastructure is introduced incrementally with the feature that consumes it.

## Local platform

The development stack contains PostgreSQL, Redis, single-node Kafka in KRaft mode and MinIO. Values in `.env.example` are development-only placeholders.

```text
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml up -d --wait
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml ps
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml down
```

Copy `.env.example` to an ignored `.env` and change values when defaults conflict. Add `--volumes` to `down` only when intentionally resetting all local platform data.

Without a local Go 1.24+ toolchain, run Booking Engine tests through Docker after its daemon is available:

```text
docker run --rm -v ${PWD}:/workspace -w /workspace/services/booking-engine golang:1.24 go test ./...
```
