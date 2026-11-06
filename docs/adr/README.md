# Architecture Decision Records

Each ADR records one decision that a reviewer could reasonably question. They use a short format:

- **Context:** the forces at play.
- **Decision:** what was chosen.
- **Consequences:** what it costs and what it buys.
- **Alternatives considered:** what was rejected and why.
- **Failure modes:** how the decision can go wrong, and what catches it.

| # | Decision | Status |
|---|---|---|
| [0001](0001-service-boundaries-and-data-ownership.md) | Service boundaries and data ownership | Accepted |
| [0002](0002-messaging-with-rabbitmq-client.md) | Messaging on RabbitMQ.Client instead of Wolverine | Accepted |
| [0003](0003-transactional-outbox-and-idempotent-consumers.md) | Transactional outbox and idempotent consumers | Accepted |
| [0004](0004-preventing-overbooking.md) | Preventing overbooking with optimistic concurrency | Accepted |
| [0005](0005-seat-availability-sync-check-async-projection.md) | Seat availability: sync sellability check + async projection | Accepted |
| [0006](0006-checkout-saga-orchestration.md) | Checkout saga orchestrated by Booking | Accepted |
| [0007](0007-retry-and-dead-letter-topology.md) | Message retries and dead-lettering | Accepted |
| [0012](0012-code-organisation.md) | Code organisation: one project per service, vertical slices | Accepted |
