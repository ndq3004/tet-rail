# Product Brief — High-Concurrency Tet Holiday Train Ticketing

## 1. Product summary

**System name:** High-Concurrency Online Ticket Reservation System  
**Working name:** TetRail

TetRail supports searching, holding, booking, and paying for train tickets during Tet holiday peaks. It prioritizes:

1. Never selling the same seat twice on an overlapping journey.
2. Regulating sudden traffic from tens of thousands of concurrent users.
3. Accepting bursts of millions of booking commands per second and processing results asynchronously; schedule and seat-map reads may degrade while the booking write path is protected.

The first release uses a payment simulator while preserving provider-like webhook, idempotency, and reconciliation contracts for future VNPay, MoMo, or bank integrations.

## 2. Scope and assumptions

### MVP scope

- Sell whole-trip tickets or configured non-overlapping segments; each request selects at most four seats in one carriage.
- Registration, login, and passenger-profile management.
- Station, route, trip, fare, and seat-map search.
- Rate limiting and a virtual waiting room.
- Configurable seat holds, defaulting to 10 minutes.
- Order creation and simulated successful, failed, or timed-out payment.
- QR electronic tickets, PDFs, and simulated/email-adapter notifications.
- Administrative management of schedules, carriages, seats, and fares, plus basic reporting.
- Load, seat-contention, and observability testing.

### Out of MVP scope

- Real payment gateways and real refunds.
- Loyalty, complex vouchers, ancillary sales, and meal selection.
- Production-grade data warehouse/ClickHouse; the MVP uses a simple reporting read model.
- Multi-region active-active deployment.

### Business assumptions

- A trip is a specific train journey on a route at a scheduled date and time.
- A seat may be sold on multiple non-overlapping segments of one trip. Locks and constraints operate on occupied segments, not only `seat_id`.
- Price is snapshotted into the order when the hold is used so later fare changes do not alter an active checkout.
- When a hold expires, the seat becomes available again. A late successful payment enters exception handling and never issues a ticket automatically.

## 3. Success metrics and non-functional requirements

- **Correctness:** no two valid tickets overlap on the same seat and journey segment.
- **Booking ingress:** at least 1,000,000 hold/booking commands per second across the cluster during a defined burst. This includes commands rejected for sold-out/conflict outcomes, not one million successful sales.
- **Booking acknowledgement:** p99 below 200 ms for preliminary validation, durable command append, and `202 Accepted`.
- **Booking completion:** a separate queue-lag SLO, initially p99 below two seconds within the designed burst envelope.
- **Read latency:** schedule and seat-map APIs have lower priority than booking and may return timestamped stale projections or controlled degradation.
- **Availability:** 99.9% target for search and booking flows in the MVP.
- **Load evidence:** disclose burst duration, hot-trip ratio, conflict ratio, payload size, accepted/rejected ratio, partitioning, and queue-drain time. Microbenchmark totals or non-durable traffic do not prove the target.
- **Physical limits:** successful holds/bookings cannot exceed available seat-segments; excess commands must be rejected quickly.
- **Fairness:** waiting-room admission is ordered; tokens are signed, scoped, expiring, and replay-resistant.
- **Security:** JWT/OIDC, customer/admin authorization, protected sensitive data, and no full National IDs in logs.
- **Auditability:** hold, order, payment, and ticket transitions are traceable by correlation ID.
- **Recovery:** events can be replayed; consumers are idempotent; failures use DLQ and replay procedures.

## 4. System responsibilities

The architecture has five core services. Each owns its schema/database logic; no service queries another service's tables directly. HTTP/REST handles synchronous calls and Kafka handles asynchronous business messages.

### 4.1 Gateway & Traffic Control

- Reverse proxy and routing.
- Edge JWT signature validation and propagation of user context.
- Rate limiting by IP, user, and endpoint.
- Virtual waiting room based on saturation, queue depth, latency, and error rate.
- Early rejection only when a sufficiently fresh projection proves a trip/carriage sold out; final seat allocation remains with the Booking Engine.
- Correlation IDs, telemetry, and timeout policies.

Gateway validation does not replace service-level authorization. Admission tokens are signed, scoped, expiring, and replay-resistant.

### 4.2 Identity, Catalog & Schedule

- Accounts, roles, and passenger profiles.
- Stations, routes, trains, carriages, seats, schedules, and fares.
- Search APIs and the seat-map read model.
- Redis cache-aside for schedules, fares, and availability projections.
- Short TTLs and domain-event invalidation. CDN is limited to static and slowly changing catalog data.

### 4.3 Booking Engine

**Runtime:** Go.

- Checks availability and creates expiring holds.
- Handles high contention for the same seats.
- Publishes `SeatHeld`, `HoldExpired`, and `HoldConfirmed` through an outbox.
- Appends commands durably to Kafka by inventory partition before returning `202`.
- Uses one logical single-writer worker per partition and in-memory seat/segment bitmaps.
- Uses Redis for idempotency, result/projection caches, and snapshots, never as the only source of truth.
- Uses PostgreSQL segment constraints as the final overbooking barrier.
- Handles same-shard multi-seat requests atomically; cross-shard requests require a saga and compensation.

### 4.4 Order & Payment

- Creates orders from valid holds and stores price/passenger snapshots.
- Controls `PENDING_PAYMENT`, `PAID`, `EXPIRED`, `CANCELLED`, and `PAYMENT_REVIEW` states.
- Creates payment attempts and processes idempotent callbacks.
- Publishes `OrderCreated`, `PaymentSucceeded`, `PaymentFailed`, and `OrderExpired`.

The simulator supports success, failure, timeout, delayed success, and duplicate callback. Development webhooks are signed, and callbacks are deduplicated by provider transaction ID.

### 4.5 Ticket, Notification & Reporting

- Consumes confirmed-payment/booking events and issues tickets idempotently.
- Generates QR codes using opaque or signed references without unnecessary personal data.
- Generates PDFs and sends email/SMS asynchronously.
- Maintains reporting read models for revenue, payment outcomes, and occupancy.

The MVP may run ticketing, notification, and reporting as modules/workers in one deployable while keeping separate namespaces and contracts.

## 5. Core domain model

| Context | Main aggregates/entities | Important rule |
|---|---|---|
| Identity | User, Passenger | Sensitive IDs are protected; users may edit only their own passengers. |
| Catalog | Station, Route, Train, Carriage, Seat, Fare | Changes must not invalidate existing order snapshots. |
| Schedule | Trip, Stop, SeatAvailabilityProjection | Projection is eventually consistent and cannot decide final allocation. |
| Booking | SeatHold, HeldSegment | Holds expire; only one active hold may occupy a segment at a time. |
| Ordering | Order, OrderItem | Price/passenger data are snapshotted; transitions are controlled. |
| Payment | PaymentAttempt, PaymentWebhook | Idempotent by key/transaction ID; callbacks are authenticated. |
| Ticketing | Ticket, QRPayload | One active ticket per order item. |
| Reporting | SalesFact, OccupancyReadModel | Reads events/CDC and never runs heavy queries on transactional paths. |

## 6. Standard business flow

1. The client enters through the Gateway and may receive a waiting-room position under overload.
2. After admission, the user searches trips and seat maps.
3. The Booking Router durably appends `HoldRequested` to Kafka and returns `202` with `request_id`; the Go shard worker later publishes `SeatHeld` or `HoldRejected`.
4. Order & Payment creates a `PENDING_PAYMENT` order and payment attempt.
5. The simulator produces success/failure/timeout behavior.
6. Timely successful payment requests booking confirmation in PostgreSQL.
7. Database constraints reject duplicates. A successful confirmation publishes `BookingConfirmed`/`PaymentSucceeded`; conflicts move to `PAYMENT_REVIEW` and do not issue tickets.
8. Ticket workers issue QR/PDF assets and send notifications idempotently.

## 7. Technology direction

- .NET / ASP.NET Core for Gateway, Identity/Catalog/Schedule, Order/Payment, and Ticket/Notification/Reporting.
- Go for the Booking Engine using lightweight HTTP routing, Kafka partition processing, bounded goroutines, Redis snapshots/caches, PostgreSQL via `pgxpool`, and OpenTelemetry.
- React + TypeScript for the web client.
- PostgreSQL for service-owned transactional data, Redis for ephemeral/cache state, Kafka for durable commands/events, and MinIO/S3 for ticket PDFs.
- OpenTelemetry, Prometheus/Grafana, and structured logs for observability.

## 8. Important risks

- Hot trips and hot seats create partition skew; capacity claims must include skewed workloads.
- Cross-shard multi-seat selection requires saga compensation; the preferred fast path remains one carriage/shard.
- CCU is not TPS; load models must state browse/hold/pay ratios and contention.
- Redis loss must be recoverable from durable records or replay.
- Late successful payments require review/compensation to avoid charging without a valid ticket.

## 9. MVP completion criteria

- Search-to-QR works end to end with the payment simulator.
- Contention tests prove that only one request can confirm the same seat/segment.
- Duplicate requests and webhooks do not duplicate orders, charges, allocations, or tickets.
- Expired holds release inventory for reuse.
- A defined load test proves at least 1,000,000 durable booking acknowledgements per second across the cluster and reports queue lag/completion SLO.
- Dashboards show latency, throughput, errors, queue depth, active holds, and payment outcomes.
