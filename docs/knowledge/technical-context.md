# Technical Context

Primary sources: [`../architecture.md`](../architecture.md) and [`../delivery-plan.md`](../delivery-plan.md).

## Core services

| Service | Runtime | Ownership |
|---|---|---|
| Gateway & Traffic Control | .NET / ASP.NET Core / YARP | Routing, edge JWT validation, rate limiting, and waiting room. |
| Identity, Catalog & Schedule | .NET / ASP.NET Core | Users/passengers, stations, routes, trips, carriages, seats, and fares. |
| Booking Engine | Go | Command routing, shard single writers, hold/expire/confirm, and allocation. |
| Order & Payment | .NET / ASP.NET Core | Order state, price snapshots, payment attempts, and webhooks. |
| Ticket, Notification & Reporting | .NET + workers | QR/PDF, delivery, and reporting read models. |

## Platform and data

- PostgreSQL: confirmed transactional state; each service owns its schema/database logic.
- Kafka: durable booking command log and versioned domain events.
- Redis: rate limiting, idempotency/result cache, projections, and snapshots; it is not the sole correctness barrier.
- MinIO/S3: ticket PDFs.
- Observability: OpenTelemetry, Prometheus/Grafana, and structured logs.
- Local/test: Docker Compose, Testcontainers, xUnit, Go testing/race/benchmarks, and k6.

## Non-negotiable boundaries

- A service must not read another service's tables directly.
- A booking partition has one logical writer; the PostgreSQL unique constraint remains the final barrier.
- Slow work such as payment, PDF generation, notification, and reporting stays off the booking ingress critical path.
- Concurrency must be bounded and use timeouts and backpressure.
- The one-million-command target is measured at durable Kafka acknowledgement across the cluster, not as tickets sold.

## Quality gates by change type

- .NET: build and relevant unit, integration, and contract tests.
- Go Booking Engine: `go test` and race detector; hot-path changes also require before/after benchmarks.
- Contracts: producer/consumer compatibility tests.
- Concurrency/payment: retry, duplicate, timeout, delayed callback, and failure paths.
- Load claims: disclose burst duration, partitions, replication, payload, skew, accepted/rejected ratio, and queue-drain time.

## Repository layout

- `services/`: five core backend deployables; each service owns its source and specialized tests.
- `web/`: React + TypeScript client.
- `contracts/`: language-neutral OpenAPI, AsyncAPI, and schemas.
- `infrastructure/`: local orchestration, telemetry, and deployment assets.
- `tests/`: cross-service contract, end-to-end, and load tests.
- `docs/features/`: implementation plans; `docs/knowledge/`: retrieval context.
