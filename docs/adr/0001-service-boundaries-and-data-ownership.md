# ADR-0001: Service boundaries and data ownership

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

TicketHub sells seats for events. The hard problem is selling a seat at most once while customers race for it, payments fail, and holds expire. The system is built as microservices on purpose: it is a portfolio project about distributed systems. That only holds up if the boundaries follow the data, and not the nouns in the domain.

## Decision

Four services, each owning one PostgreSQL database that no other service reads:

| Service | Owns | Why it is separate |
|---|---|---|
| **Catalog** | Venues, seats, events, prices, plus a read-only projection of seat status | Read-heavy, cacheable, and changes rarely. It can scale and fail independently of checkout. |
| **Booking** | Seat allocations and reservations | This is where contention happens. A seat claim and the reservation that holds it must change in **one ACID transaction**. |
| **Payments** | Payment attempts | It talks to a third party with its own latency and failure profile, and must be idempotent towards it. |
| **Notifications** | What it has already sent | A pure side effect. It must never slow down or break a purchase. |

### Who decides whether a seat is available

Availability has two halves, and each half has exactly one owner:

- **Catalog** answers *"can this seat be sold for this event, and at what price?"* That covers whether the seat exists, whether it is blocked, and whether the event is on sale. Booking asks over gRPC at the moment of the hold, because it needs a fresh, authoritative price.
- **Booking** answers *"is this seat free right now?"*, in its `seat_allocations` table.

The seat map that customers browse is a **projection** held in Catalog and fed by Booking's events. It is eventually consistent by design. The authoritative check happens when the hold is created.

## Consequences

- There is no distributed transaction on the hot path. The contended write is a single-database transaction.
- The seat map can be stale for a moment. A customer may click a seat that was just taken and get a `409`. That is acceptable, and it is also what large ticketing sites do.
- Booking depends on Catalog being up when a hold is created. This is mitigated by timeouts, retries and a circuit breaker on that one call (ADR-0008).
- There are four databases to migrate and operate. In development they are four databases on one PostgreSQL server.

## Alternatives considered

- **Catalog owns seat status, and Booking asks it to hold seats.** Every hold would then span two databases, and would need its own saga and compensation just to claim a seat. Rejected: the critical section would become distributed.
- **Replicate prices into Booking through events, with no gRPC call.** This gives better autonomy, but holds would be priced from stale data, and the brief asks for a synchronous check. Rejected for now. It remains the natural next step if Catalog availability ever became a problem.
- **A modular monolith.** This is honestly the better default for a team this size. The README's trade-offs section describes what that version would look like.

## Failure modes

- *Catalog is down:* holds fail fast with `503` and `Retry-After`. Browsing cached seat maps keeps working.
- *The projection lags:* the customer sees a free seat that is already taken and gets a `409`, so no harm is done.
