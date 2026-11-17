# 010 — Electronic ticket issuance and delivery

Status: `done`  
Owner: `Codex`  
Depends on: `008`

## Outcome

Paid orders expose one electronic ticket per item, with QR validation and private PDF downloads.

## Scope

- Kafka-driven issuance, authenticated ticket discovery, QR validation, PDF storage, simulated delivery, and durable retries.

## Acceptance criteria

- [x] Paid orders issue one ticket per item under concurrent replay.
- [x] Owners can list and read tickets and obtain expiring PDF URLs.
- [x] QR codes contain opaque references which can be validated without exposing passenger data.
- [x] PDF and notification failures retry and can enter a durable dead-letter queue independently of payment.

## Design and impact

- Ticketing owns its PostgreSQL schema and private object bucket. It reads immutable items through an Order & Payment v1 internal endpoint after consuming PaymentSucceeded.v1, including older events.
- Ticket metadata and QR validation use the versioned tickets HTTP contract. TicketIssued.v1 is published through a transactional outbox.
- Forward-fix by retaining inbox, tickets, and delivery jobs while rolling workers forward. Retry dead-letter jobs with their original ticket IDs and object keys.

## Implementation

- [x] Implement issuance and customer access.
- [x] Implement PDF and simulated notification delivery.
- [x] Update contracts and operations documentation.

## Verification

- [x] Build and test issuance replay, authorization, QR/PDF, and delivery retries.
- [x] Verify PostgreSQL, Kafka, and object storage integration.

## Progress notes

- Solution build and 31 .NET tests passed. Concurrent replay, PDF and notification retries, dead-lettering, and requeue were verified against disposable PostgreSQL.
- The live Booking, Order & Payment, Kafka, Ticketing and MinIO flow verified owner access, QR validation, deduplicated issuance and signed PDF retrieval.
- [Deployment and recovery instructions](../testing/010-electronic-ticket-issuance-and-delivery.md)

## Remaining risks

- Delivery uses the MVP simulated notification adapter. The live check uses trusted local service endpoints.
