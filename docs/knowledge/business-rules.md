# Business Rules and Invariants

Primary sources: [`../product-brief.md`](../product-brief.md) and [`../architecture.md`](../architecture.md).

## Inventory and booking

- No two confirmed allocations may overlap on `(trip_id, seat_id, segment_index)`.
- A journey interval uses the half-open form `[from_stop_order, to_stop_order)`.
- Two bookings conflict when `new_from < existing_to AND existing_from < new_to`.
- A seat may be resold on non-overlapping segments of the same trip.
- The seat map/projection is read-only and may only support an early rejection when sufficiently fresh; the Booking Engine decides seat allocation.
- `202 Accepted` confirms only that the command was durably recorded, not that a seat was held.
- Only a `HELD` result containing `hold_id` and `expires_at` confirms a successful hold.
- In the MVP, a multi-seat request contains at most four seats in the same carriage and must be all-or-nothing.
- A hold expires after 10 minutes by default unless confirmed.

## Orders, payments, and tickets

- An order may be created only from an active hold; price and passenger data are snapshotted into the order.
- Payment webhooks must be authenticated and deduplicated by provider transaction ID.
- Repeated callbacks must not create additional orders, charges, allocations, or tickets.
- A successful payment arriving after hold expiry moves to `PAYMENT_REVIEW`; it does not issue a ticket automatically.
- Tickets are issued idempotently only after booking confirmation.
- Each order item may have only one active ticket.

## Security and audit

- Customers may access or modify only their own data; administrative actions require separate authorization.
- Never log a complete National ID, secrets, tokens, or sensitive payment data.
- Important requests and events carry `correlation_id`; events also carry `event_id`, `causation_id`, `occurred_at`, and a version.
