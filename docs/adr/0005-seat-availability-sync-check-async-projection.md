# ADR-0005: Seat availability: a synchronous sellability check plus an asynchronous seat-map projection

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

Two different questions are both called "availability":

1. When creating a hold: *can I sell these seats, and for how much?* The answer must be authoritative and fresh.
2. When browsing: *which seats look free?* The answer must be fast and cheap, under heavy read traffic.

ADR-0001 makes Booking the owner of who holds which seat, and Catalog the owner of venues, events and prices.

## Decision

- **Hold time, synchronous:** Booking calls Catalog's gRPC `SeatSellability.CheckSeats`. Catalog answers:
  - whether the event exists and is inside its sales window
  - for each seat: whether it exists, whether it is sellable (not blocked, and its section is priced), its label, and its price in minor units.

  Booking never trusts a price sent by the client. Booking itself then decides whether each seat is *free*.
- **Browse time, asynchronous:** Catalog keeps a projection, `EventSeatStatus`, fed by Booking's `SeatAvailabilityChanged` events. The seat map joins the venue layout with this projection. A seat with no row is `Available`.
- **gRPC transport:** gRPC is used here because it is a typed internal contract with HTTP/2 multiplexing. Catalog owns `catalog.proto`; Booking compiles a client from the same file.
- **Separate gRPC endpoint:** cleartext HTTP/2 (h2c) cannot share a port with HTTP/1.1 without TLS, so Catalog exposes a dedicated `grpc` endpoint next to its REST one.

## Consequences

- The seat map is **eventually consistent**. A seat can appear free for a moment after someone has held it. The worst case is a `409` and a refresh, which is acceptable and common in ticketing.
- Holds depend on Catalog being reachable. That call gets a timeout, retries and a circuit breaker (ADR-0008); when Catalog is down, holds fail fast with `503`.
- The projection must tolerate duplicates and reordering. The inbox handles duplicates (ADR-0003), and the per-seat `Version` handles order.

## Alternatives considered

- **Seat map served by Booking**, which is always consistent. Rejected: browsing traffic would hit the database that has to stay fast for holds.
- **Catalog calls Booking for seat status on each seat-map request.** Rejected: it couples the read path to Booking's availability.
- **Replicate prices to Booking through events, with no gRPC call.** This is more autonomous, but the hold would use possibly stale prices. Kept as a future option.

## Failure modes

| Failure | What happens |
|---|---|
| Catalog down | Holds return `503` with `Retry-After`. Cached seat maps still serve (ADR-0009). |
| Projection lagging (broker slow) | Customers see a stale map. Holds stay correct. |
| Events applied out of order | The higher `Version` wins (`Projection_applies_out_of_order_events_by_version`). |
