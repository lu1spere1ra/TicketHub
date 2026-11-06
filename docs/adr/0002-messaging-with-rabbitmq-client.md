# ADR-0002: Messaging on RabbitMQ.Client instead of Wolverine

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

The services exchange commands and events over RabbitMQ, with these requirements:

- a transactional outbox in Booking
- idempotent consumers
- retries and dead-lettering
- trace propagation across the broker

MassTransit, MediatR and AutoMapper are excluded. The two realistic options are **Wolverine**, a framework that provides all of the above, and **RabbitMQ.Client** (7.x) plus our own thin layer.

## Decision

Use **RabbitMQ.Client 7.x** directly, with a small building block in `TicketHub.BuildingBlocks/Messaging`. It provides:

- An outbox table and a relay that publishes rows with publisher confirms.
- An inbox table that deduplicates within the consumer's database transaction.
- One retry queue with a TTL, followed by a dead-letter queue.
- W3C trace context stored with each outbox row and carried in AMQP headers.

Aspire's `Aspire.RabbitMQ.Client` integration supplies the `IConnection`, with health checks and connection-string wiring.

## Consequences

- Every behaviour the system relies on is in this repository, at a few hundred lines that can be read in one sitting. Each guarantee is pinned by a test against a real broker and a real database.
- We own the edge cases that a framework would hide:
  - connection recovery (RabbitMQ.Client's automatic recovery covers most of it)
  - poison messages
  - consumer prefetch
  - what happens when the relay crashes between publishing and marking a row sent
- There is less ceremony than in a framework, because there are no conventions to learn. A handler is a class with a `HandleAsync` method.

## Alternatives considered

- **Wolverine:** a durable outbox and inbox on PostgreSQL, sagas and dead-letter storage, all mature. For a product team this would very likely be the right call. Here it would turn the patterns this project is meant to demonstrate into configuration. It is also a mediator and a messaging framework at once, which overlaps with the "no MediatR" constraint.
- **MassTransit:** excluded by the brief. Since v9 it also requires a commercial licence.

## Failure modes

- *Relay crashes after publishing, before marking the row sent:* the message is published again. Consumers deduplicate on the message ID, so delivery is at least once with an exactly-once effect.
- *Broker unavailable:* outbox rows accumulate and are published when the broker returns. Business transactions are unaffected.
- *Handler bug (poison message):* the message is retried a bounded number of times, then dead-lettered and shown on the admin endpoint. It never loops forever.
