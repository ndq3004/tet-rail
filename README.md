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

Each service currently exposes only `GET /health`. Business endpoints and infrastructure integrations are added through plans in `docs/features/`.

## Local dependencies

```text
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml up -d --wait
docker compose --env-file infrastructure/compose/.env.example -f infrastructure/compose/compose.yml ps
```

See `infrastructure/README.md` for stopping/resetting the stack and running Go tests through Docker when Go is not installed locally.
