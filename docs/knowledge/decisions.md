# Decisions and Open Questions

Primary source: [`../architecture.md`](../architecture.md), section "Accepted architecture decisions," and [`../product-brief.md`](../product-brief.md).

## Accepted decisions

| ID | Decision | Consequence |
|---|---|---|
| D-001 | Seats are sold by segment and may be reused on non-overlapping segments. | Allocation and constraints use trip, seat, and segment. |
| D-002 | The Booking Engine is the only Go service; the other four backend core services use .NET. | Go/.NET contracts remain language-independent. |
| D-003 | Booking commands pass through Kafka partitions and logical single writers. | Ingress returns `202`; results are processed asynchronously. |
| D-004 | A PostgreSQL unique constraint is the final overbooking barrier. | Cache, bitmap, or locks alone cannot establish correctness. |
| D-005 | Redis stores cache, idempotency, results/projections, and snapshots; it is not the only source of truth. | The system must support rebuild/replay and controlled degradation. |
| D-006 | Domain events use outbox/inbox and idempotent consumers. | Retry/replay must not duplicate side effects. |
| D-007 | Reporting uses a separate read model. | Reporting queries must not slow the transactional path. |
| D-008 | The payment simulator keeps a provider-like webhook contract. | It supports success, failure, timeout, delayed success, and duplicate callbacks. |
| D-009 | One million commands per second means durable command acknowledgements across the cluster. | Load reports must separately show completion lag and workload assumptions. |
| D-010 | AWS Cognito User Pools is the MVP OIDC provider; Gateway and Catalog validate Cognito JWTs directly. | The MVP does not operate OpenIddict or Keycloak. Cognito issuer, audience, JWKS, groups-to-role mapping, refresh, and revocation are provider configuration concerns. |

## Open questions

- What official burst duration and completion SLO will the acceptance test use?
- Will the final inventory shard key use carriage or seat bucket, and what is the bucket split threshold?
- What production refund policy will replace the simulator's `PAYMENT_REVIEW` handling?

When an answer is confirmed, convert it into a decision with an ID. If it has broad impact, create an ADR and link it from the table above.
