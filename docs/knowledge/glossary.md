# Domain Glossary

Primary sources: [`../product-brief.md`](../product-brief.md) and [`../architecture.md`](../architecture.md).

| Term | Meaning in TetRail |
|---|---|
| Station | A railway station and a stop on a route. |
| Route | An ordered sequence of stations. |
| Trip | A specific train journey on a route at a scheduled date and time. |
| Stop order | A station's position within a route or trip. |
| Segment | The section between two consecutive stations; the smallest unit used to determine seat occupancy. |
| Carriage | A train carriage containing a set of seats. |
| Seat | A physical seat in a carriage; it may be resold on non-overlapping segments. |
| Inventory shard | A stable inventory group based on trip and carriage/seat bucket, processed by one logical writer at a time. |
| Booking command | An asynchronous request such as hold or confirm, durably appended before returning `202`. |
| Booking request | Processing result tracking identified by `request_id`. |
| Hold | Temporary ownership of one or more seats/segments, identified by `hold_id` and an expiry time. |
| Allocation | Confirmed occupancy of a seat on individual segments; a unique constraint is the final overbooking barrier. |
| Order | A purchase order created from a valid hold and containing price and passenger snapshots. |
| Payment attempt | A payment attempt whose callback must be authenticated and deduplicated. |
| Ticket | An electronic ticket issued after payment and booking are confirmed. |
| Projection | An eventually consistent read model; it is not the source of truth for seat allocation. |
| Admission token | A signed, scoped, expiring token issued by the waiting room. |
