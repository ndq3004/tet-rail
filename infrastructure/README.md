# Infrastructure

Local orchestration, telemetry and deployment assets belong here.

- `compose/`: local PostgreSQL, Kafka, Redis, MinIO and observability stack.
- `observability/`: dashboards, collectors and alert rules.

Infrastructure is introduced incrementally with the feature that consumes it.

## Local platform

The development stack contains PostgreSQL, Redis, single-node Kafka in KRaft mode, Kafka UI, and MinIO. Values in `.env.example` are development-only placeholders.

```text
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml up -d --wait
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml ps
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml down
```

After the stack is running, open [Kafka UI](http://localhost:8082) to inspect the local `tetrail-local` cluster, topics, records, and consumer groups. Port `8082` is the default and can be changed with `TETRAIL_KAFKA_UI_PORT`. It is unauthenticated and intended only for local development.

Copy `.env.example` to an ignored `.env` and change values when defaults conflict. Add `--volumes` to `down` only when intentionally resetting all local platform data.

On a fresh PostgreSQL volume, Compose automatically applies the Catalog trip-search schema and development seed from `services/catalog/Migrations/`. For an existing volume, apply both SQL files manually or recreate only after intentionally accepting local data loss.

Without a local Go 1.24+ toolchain, run Booking Engine tests through Docker after its daemon is available:

```text
docker run --rm -v ${PWD}:/workspace -w /workspace/services/booking-engine golang:1.24 go test ./...
```

## Aspire application processes

Compose owns the local stateful platform and must be running before Aspire starts the application processes:

```text
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml up -d --wait
dotnet run --project infrastructure/TetRail.AppHost/TetRail.AppHost.csproj
```

The Aspire dashboard shows Gateway, Catalog, Order & Payment, Ticketing, and Booking Engine. It does not replace Compose or manage PostgreSQL, Redis, Kafka, or MinIO.

Gateway, Catalog, Order & Payment, and Ticketing each show a **Swagger UI** link in the dashboard. The link opens that service's development-only `/swagger` page; its generated document is available at `/openapi/v1.json`. Booking Engine remains health-only until it publishes an OpenAPI document.
