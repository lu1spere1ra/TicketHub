# ADR-0003: Transactional outbox and idempotent consumers

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

When Booking holds seats it must do two things: commit the hold in PostgreSQL, and tell other services (Catalog's seat map, later Payments and Notifications) through RabbitMQ. PostgreSQL and RabbitMQ cannot share a transaction. If both are done naively, one of them eventually happens without the other:

- **Commit, then publish:** if the process dies in between, the hold exists but nobody hears about it. The seat map shows the seat as free forever.
- **Publish, then commit:** if the commit fails, consumers react to something that never happened.

## Decision

**Outbox, on the producer side.** A handler calls `IOutbox.Enqueue(message)`. This adds a row to `OutboxMessages` in the *same* `DbContext`, so the message commits atomically with the business change. If the transaction rolls back, there is no message.

`OutboxRelay` is a background service in every publishing service:

1. Polls every 500 ms.
2. Claims up to 100 unsent rows with `SELECT … FOR UPDATE SKIP LOCKED`, so several replicas never publish the same row concurrently.
3. Publishes each row with **publisher confirms**.
4. Marks the rows sent and commits.

Sent rows are purged after 24 h.

**Inbox, on the consumer side.** Every delivery runs in one database transaction:

1. `INSERT INTO "InboxMessages" (message_id, consumer) … ON CONFLICT DO NOTHING`.
2. If no row was inserted, the message was already processed: commit, ack, skip.
3. Otherwise, run the handler on the same scoped `DbContext`, `SaveChanges`, commit, **then** ack.

The insert happens first, not a "does it exist?" check. A concurrent duplicate therefore blocks on the primary key until the first transaction finishes, and then inserts nothing. Duplicates are serialised rather than raced.

## Consequences

- **Delivery is at least once.** The relay can crash after the broker confirms but before it marks the row sent, and the next relay pass republishes. That is expected, and consumers absorb it.
- **Effects are exactly once** for handlers whose effects are database writes in the same transaction as the inbox row.
- **Side effects outside the database** (sending an email) can still happen twice if the process dies between sending and committing. Notifications accepts this; avoiding it would need idempotency support from the email provider.
- **Latency:** up to one poll interval (500 ms) between commit and publish. That is fine for a seat map; it would be wrong for a trading system.
- **Ordering:** not guaranteed (replicas, retries). Consumers must be order-tolerant. Catalog's projection applies a seat change only if its `Version` is newer than the stored one.
- **Shared schema:** every service's database has two extra tables, `OutboxMessages` and `InboxMessages`. The inbox table grows over time; a retention job is a natural next step.

## Alternatives considered

- **Two-phase commit / distributed transactions:** not supported by RabbitMQ, and a liability even where available.
- **Change data capture** (Debezium on the WAL): no polling and strict ordering, but another piece of infrastructure to run. That is overkill here.
- **Listen/notify instead of polling:** lower latency, but more moving parts. Polling an index of unsent rows every 500 ms is cheap.
- **Consumer-side "check then insert" deduplication:** races under concurrent redelivery. Rejected in favour of insert-first.

## Failure modes

| Failure | What happens |
|---|---|
| Broker down | Rows accumulate unsent; the relay logs and retries each poll. Business writes keep working. |
| Relay crash mid-batch | The transaction rolls back; rows stay unsent and are republished; consumers deduplicate. |
| Handler throws | Handler changes and the inbox row roll back together; the message is retried (ADR-0007). |
| Same message delivered twice at once | The second delivery blocks on the inbox primary key, then becomes a no-op. |

Tests in `tests/TicketHub.BuildingBlocks.Tests/Messaging` pin each row of this table against real PostgreSQL and RabbitMQ.
