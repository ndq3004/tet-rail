# 022 — Booking shard snapshot and recovery

Status: `in-progress`
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

- [ ] A snapshot is partition-scoped, versioned, and contains its source offset and owner epoch.
- [ ] Restart restores the valid snapshot and replays subsequent commands in partition order.
- [ ] A stale owner cannot claim or release inventory after an epoch advances.
- [ ] Recovery/rebalance tests preserve all-or-nothing outcomes and PostgreSQL remains the final allocation barrier.

## Design and impact

- Service/module: Booking Engine owns shard guard snapshots; PostgreSQL holds and allocations remain authoritative.
- API/event contract: no public contract change; snapshot format is internal and versioned.
- Data/migration: snapshot storage must be forward-compatible and recoverable by Kafka replay.
- Security/observability: metrics expose snapshot age, restore duration, replay count, stale-owner fences, and recovery failures without logging request payloads.
- Key decisions: do not activate an assignment until its snapshot/replay catch-up completes; an unavailable snapshot store falls back to replay and never grants a hold solely from cached state.

## Implementation

- [x] Define snapshot persistence boundary and versioned Redis record.
- [ ] Wire Kafka assignment epoch, snapshot restore, and replay into the hold worker.
- [ ] Add restart/rebalance component and PostgreSQL/Kafka integration tests.
- [ ] Update the US-04 runbook and knowledge summary.

## Verification

- [x] Unit/component: `cd services/booking-engine; go test ./...` — passed on 2026-09-25.
- [ ] Integration: `go test -tags=integration ./...`
- [ ] Resilience: controlled worker restart and partition-owner epoch test using local Kafka/PostgreSQL.

## Remaining risks

- Load evidence is tracked separately after recovery behavior is verified.

## Progress notes

- 2026-09-25: Added a versioned, partition-scoped Redis snapshot boundary that validates mapping version, partition, source offset, epoch, and unique occupied keys. It is intentionally not authoritative; a missing or invalid snapshot must fall back to Kafka replay.
