# ADR-0007: Message retries and dead-lettering

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

Handlers fail for two different reasons, which need different treatment:

- **Transient failures:** a database deadlock, a payment provider outage, an SMTP timeout, or a concurrency conflict in the saga. Retrying after a short pause usually succeeds.
- **Permanent failures:** a payload that cannot be deserialised, or a bug. Retrying forever blocks the queue and burns CPU, and a human has to look.

## Decision

The topology for each consumer queue `Q`:

```
tickethub.events (topic) ──routing key = message type──▶ Q
Q ── handler throws, attempt < 5 ──▶ publish copy to Q.retry (x-attempt + 1), ack original
Q.retry (x-message-ttl = 5 s, dead-letter-exchange = "", routing key = Q) ──TTL expires──▶ Q
Q ── attempt = 5, or payload unreadable ──▶ tickethub.dead-letters (with error headers), ack original
tickethub.dead-letters ──▶ Booking's DeadLetterArchiver ──▶ DeadLetters table ──▶ GET /admin/dead-letters
```

The rules behind it:

- **The retry copy goes back to the same queue only.** It is routed through the default exchange by queue name, not back through the topic exchange, so a retry never re-delivers to other services that already handled the message.
- **There is one retry queue with a fixed delay per consumer queue.** A per-message TTL in a single shared queue would cause head-of-line blocking (RabbitMQ only expires messages at the head). One fixed delay is enough here; exponential backoff would need one queue per level.
- **The message ID is preserved across retries,** so the inbox still deduplicates.
- **Republishing happens on a channel with publisher confirms,** and the original is acked only after the copy is confirmed. If the republish fails, the original is nacked back onto the queue. A message is never lost between the two.
- **Unreadable messages skip retries.** No number of attempts will fix them.
- **Dead letters carry headers** for the original queue, the attempt count, the exception type and message, and the failure time. One archiver drains them into a table, so the admin endpoint can list failures from every service in one place.

## Consequences

- A transient failure costs a few seconds of delay, not a lost message.
- A poison message costs at most five attempts (about 20 seconds), then it is parked with enough context to diagnose.
- Retries reorder messages relative to each other, which is one more reason consumers must be order-tolerant (ADR-0003).
- Replaying a dead letter is a manual operation (publishing the payload back to its queue). An endpoint for it was left out on purpose to keep the scope small.

## Alternatives considered

- **`nack(requeue: true)`:** immediate redelivery with no delay, and an infinite loop for poison messages.
- **Broker-native dead-lettering via `x-dead-letter-exchange` on rejection:** simple, but it loses the exception details and gives no attempt counter without parsing `x-death`.
- **The delayed-message exchange plugin:** not in the default RabbitMQ image, which would be one more thing to install and operate.
