# 022 — Booking shard snapshot and recovery

Status: `done`
Owner: `Codex`
Depends on: `021`

## Outcome

An assigned Booking Engine partition restores an epoch-fenced inventory guard from a durable snapshot and replays only the remaining ordered commands before serving work.

## Scope

### In scope

- Persist versioned shard snapshots with Kafka partition, offset, mapping version, owner epoch, and occupied segment keys.
- Restore a snapshot only for the matching mapping version and assigned partition, then replay commands after its stored offset.
- Fence stale owners and prove restart/rebalance recovery does not report a false `HELD` result.

### Out of scope

- Dynamic Kafka partition-count migration and cluster-scale load certification.

## Acceptance criteria

- [x] A snapshot is partition-scoped, versioned, and contains its source offset and owner epoch.
- [x] Restart restores the valid snapshot and replays subsequent commands in partition order.
- [x] A stale owner cannot claim or release inventory after an epoch advances.
- [x] Recovery/rebalance tests preserve all-or-nothing outcomes and PostgreSQL remains the final allocation barrier.

## Design and impact

- Service/module: Booking Engine owns shard guard snapshots; PostgreSQL holds and allocations remain authoritative.
- API/event contract: no public contract change; snapshot format is internal and versioned.
- Data/migration: snapshot storage must be forward-compatible and recoverable by Kafka replay. The additive PostgreSQL epoch table fences mutations in the same transaction as the hold result. A forward fix can rebuild Redis snapshots from the retained Kafka log while keeping the PostgreSQL epoch rows.
- Security/observability: metrics expose snapshot age, restore duration, replay count, stale-owner fences, and recovery failures without logging request payloads.
- Key decisions: do not activate an assignment until its snapshot/replay catch-up completes; an unavailable snapshot store falls back to replay and never grants a hold solely from cached state.

## Implementation

- [x] Define snapshot persistence boundary and versioned Redis record.
- [x] Wire Kafka assignment epoch, snapshot restore, and replay into the hold worker.
- [x] Fence worker mutations with a PostgreSQL epoch checked in the same transaction as the hold outcome. The integration test verifies that an earlier owner cannot write after a new assignment takes ownership.
- [x] Add PostgreSQL/Kafka/Redis integration coverage for worker restart, snapshot restoration, and two-member rebalance.
- [x] Update the US-04 runbook and knowledge summary.

- The local Kafka 4.0 broker rejects the previous Go client's JoinGroup request. Kafka Go 0.4.51 negotiates a supported version, and the local worker restart test passes against Kafka 4.0 and PostgreSQL.

## Verification

- [x] Unit/component: `cd services/booking-engine; go test ./...` — passed on 2026-09-25.
- [x] Integration: `go test -tags=integration ./...` — passed on 2026-09-26 against local PostgreSQL, Kafka, and Redis.
- [x] Resilience: `go test -race -count=1 -tags=integration ./...` passed with local Kafka/PostgreSQL/Redis. The restart test verifies restoration through a snapshot-only marker, waits for a new epoch after a second worker joins, and checks compensation leaves no held segment. A separate test verifies that a stale epoch cannot write.
- [x] Local load: 10 VUs for 5 seconds with an 80% configured hot-seat ratio. All 50 commands were accepted and completed (11 held, 39 rejected), with 1.17 s completion p95. The first Kafka offset-lag sample was zero across eight reported partitions, taken 2.18 s after k6 returned.
- 2026-11-13: `BenchmarkShardRouting` measured 34.96 ns/op and 0 allocations. The local k6 smoke run used 10 VUs for 5 seconds with 80% configured hot-seat traffic and recorded 50/50 durable acknowledgements at 1.04 s p95. Terminal outcomes and queue lag were not measured.

## Remaining risks

- Distributed capacity and formal burst SLO certification are tracked under US-09.

## Progress notes

- 2026-09-25: Added a versioned, partition-scoped Redis snapshot boundary that validates mapping version, partition, source offset, epoch, and unique occupied keys. It is intentionally not authoritative; a missing or invalid snapshot must fall back to Kafka replay.
- 2026-09-26: Added a Redis round-trip integration check for a partition-scoped snapshot record.
