# ADR-0006: Checkout saga orchestrated by Booking

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

A purchase spans three services:

- **Booking** holds the seats.
- **Payments** charges the card.
- **Notifications** tells the customer.

There is no distributed transaction, so each step commits locally. A failure later in the flow must be *compensated*: seats must return to sale if payment fails or the customer walks away, and money must go back if it was taken for seats we no longer hold.

## Decision

**Orchestration, with Booking as the orchestrator.** The `Reservation` aggregate *is* the saga state. Its methods are the steps, it enforces which transitions are legal, and it appends each step to a `Timeline` that the admin endpoint shows.

```
Held ──checkout──▶ AwaitingPayment ──PaymentSucceeded──▶ Confirmed   (sell seats, email)
  │                      │
  │                      ├──PaymentFailed──▶ Cancelled                (release seats, email)   ← compensation
  └──────deadline────────┴──deadline──────▶ Expired                   (release seats, email)   ← timeout
Expired/Cancelled ──late PaymentSucceeded──▶ RefundPayment            (money back)             ← compensation
```

The design rules behind it:

- **One absolute deadline:** hold time + 10 minutes. `HoldExpiryService` claims due reservations with `FOR UPDATE SKIP LOCKED`, so it can run on every replica.
- **A payment that arrives after the deadline but before expiry ran is honoured.** The seats were never released, so nobody else can have them, and confirming is safe and kinder. Once the seats *are* released, a late payment can only be refunded.
- **Concurrent steps are serialised by the reservation's version** (optimistic concurrency). If the expiry job and a payment result race, one commits. The other gets a concurrency conflict, its message is retried, and it re-reads the new state. A test runs this race repeatedly and accepts only two outcomes: *Confirmed with sold seats and no refund*, or *Expired with free seats and a refund*.
- **Every step's side effects** (seat changes, events for Catalog and Notifications, commands for Payments) are written to the outbox in the same transaction as the state change (ADR-0003). A step either fully happens or not at all.
- **Payments is idempotent per reservation.** The reservation ID is the idempotency key, both for our `Payments` table (unique index) and for the payment provider.

## Consequences

- The whole workflow can be read in one class (`Reservation`) and its state machine is unit-tested exhaustively, without infrastructure.
- Booking knows the workflow; Payments and Notifications only know their own job. Adding a step (e.g. issuing tickets) changes Booking only.
- The orchestrator is a single point of logic, though not of failure: it is stateless apart from the database, and it scales out.

## Alternatives considered

- **Choreography** (each service reacts to the previous service's event, with no orchestrator): less coupling on paper, but the flow and its compensations end up spread across services and are hard to see or test. That is a poor fit when the main risk is getting compensation right.
- **A separate saga entity next to the reservation:** cleaner in theory, but it duplicates the status and opens a gap between "saga says confirmed" and "reservation says confirmed". Merging them removes a class of bugs.
- **A grace period when a payment is in flight at the deadline:** gentler for the customer, but it means a second deadline to reason about. The "honour it if the seats were never released" rule gets most of the benefit for free.

## Failure modes

| Failure | Outcome |
|---|---|
| Payment declined | Reservation `Cancelled`, seats released, email sent. |
| Customer never pays | Reservation `Expired` at the deadline, seats released, email sent. |
| Payment succeeds after seats were released | `RefundPayment`, then `PaymentRefunded`; the timeline shows `RefundRequested` and `Refunded`. |
| Payment result delivered twice | The inbox drops it; even if it got through, `Duplicate` is a no-op. |
| Expiry and payment at the same instant | One wins by version; the other re-reads. Consistent either way. |
| Payments down | `RequestPayment` waits in its queue (and retries on errors); the reservation expires at the deadline as usual. |
