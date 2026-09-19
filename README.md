# TetRail

High-concurrency online train ticket reservation system organized as a monorepo with five core backend services and one web client.

## Repository structure

```text
services/
  gateway/                 .NET edge routing and traffic control
  catalog/                 .NET identity, catalog and schedule
  booking-engine/          Go booking hot path
  order-payment/           .NET order and payment
  ticketing/               .NET ticket, notification and reporting
web/                       React + TypeScript client
contracts/                 Language-neutral HTTP and event contracts
infrastructure/            Local orchestration and observability assets
tests/                     Cross-service contract, end-to-end and load tests
docs/                      Product, architecture, knowledge and feature plans
```

## Prerequisites

- .NET SDK 9
- Go 1.24 or newer
- Node.js 24 and npm
- Docker Desktop with Docker Compose

## Verify the skeleton

```text
dotnet build TetRail.slnx
cd services/booking-engine && go test ./...
cd web && npm install && npm run build
```

Every service exposes `GET /health`. The Catalog service also owns versioned `GET /api/v1/trips`; the Gateway exposes the public `GET /api/trips` route.

## Local dependencies

```text
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml up -d --wait
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml ps
```

See `infrastructure/README.md` for stopping/resetting the stack and running Go tests through Docker when Go is not installed locally.

## Local application orchestration

Docker Compose remains responsible for the stateful local platform. Start it first, then launch all application processes and the development dashboard with Aspire:

```text
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml up -d --wait
dotnet run --project infrastructure/TetRail.AppHost/TetRail.AppHost.csproj
```

Aspire starts Gateway, Catalog, Order & Payment, Ticketing, and the Go Booking Engine. Gateway resolves Catalog through service discovery; PostgreSQL, Redis, Kafka, and MinIO continue to use the Compose endpoints and lifecycle.

In the Aspire dashboard, open the **Swagger UI** link beside Gateway, Catalog, Order & Payment, or Ticketing to inspect and try that service's implemented endpoints. Swagger UI is local-development only. The Booking Engine does not yet publish an OpenAPI document, so it has no Swagger UI link.
