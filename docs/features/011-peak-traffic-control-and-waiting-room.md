# 011 — Peak traffic control and waiting room

Status: `done`  
Owner: `Codex`  
Depends on: `010`

## Outcome

Gateway meters new traffic while preserving checkout capacity.

## Scope

- Distributed rate limits, FIFO admission for new holds, pressure-based activation, and controlled shedding verification.

## Acceptance criteria

- [x] IP, authenticated subject, and endpoint limits return 429 with Retry-After.
- [x] Waiting-room grants are signed, short-lived, scoped to the subject and hold route, and consumed atomically.
- [x] Activation reflects latency, errors, saturation, and queue depth. Checkout bypasses the waiting room and keeps separate quotas.
- [x] Concurrent load verifies controlled shedding and checkout continuity.

## Design and impact

- Redis owns temporary quotas and FIFO membership. Gateway owns admission policy and exposes v1 waiting-room routes.
- Forward-fix by keeping signing configuration and letting temporary Redis keys expire.

## Implementation

- [x] Wire traffic middleware and queue endpoints.
- [x] Add contract and verification evidence.

## Verification

- Solution build and Booking Engine race suite passed.
- Redis integration passed across two Gateway middleware instances with signed grants, FIFO, subject isolation, concurrent replay, and expiry. All eight checkout requests reached the healthy test handler during a 200-request browse burst with controlled shedding.
- The private pressure endpoint returned a live Kafka and PostgreSQL sample. A live SQL check verified that expired and unacknowledged queue entries do not activate admission.

- The follow-up Redis integration used a live Order & Payment service and PostgreSQL for all eight checkout reads during the 200-request browse burst. The Booking pressure endpoint returned a healthy live Kafka sample with partition skew. The Go race suite includes a partition-skew test.

## Remaining risks

- The load check exercises two Gateway middleware instances against shared Redis and a live checkout backend. Production-scale throughput and latency remain unmeasured.
