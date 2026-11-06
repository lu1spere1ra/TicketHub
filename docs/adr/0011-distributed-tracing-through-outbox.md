# ADR-0011: Distributed tracing across the outbox and the broker

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

A purchase crosses several hops:

1. Gateway
2. Booking
3. Catalog (gRPC)
4. Booking's database
5. RabbitMQ
6. Payments
7. RabbitMQ again
8. Booking
9. Notifications

Tracing HTTP and gRPC is automatic with OpenTelemetry. Messages are not, and the outbox makes them harder still: the message is published **later, by a different thread** (the relay), long after the request's span has ended. A naive setup produces one trace per hop, with no link between them.

## Decision

- **Capture:** when a handler calls `IOutbox.Enqueue`, the current W3C trace context (`Activity.Current.Id` and the trace state) is stored in the outbox row.
- **Publish:** the relay starts a `Producer` span whose **parent is the stored context**, not whatever is current on the relay thread, and writes `traceparent` / `tracestate` into the AMQP headers.
- **Consume:** the consumer extracts the headers and starts a `Consumer` span with that parent. Database calls made by the handler, and anything it enqueues, join the same trace.
- **Spans** come from the `TicketHub.Messaging` `ActivitySource`, following OpenTelemetry messaging conventions (`messaging.system`, `messaging.destination.name`, `messaging.message.id`).
- **No duplicate spans:** RabbitMQ.Client's own instrumentation is disabled (`DisableTracing` in the Aspire client), because it would add a parallel set of spans that are not linked across the outbox.
- **Registration:** ServiceDefaults registers every `TicketHub.*` source and meter, plus ASP.NET Core, HttpClient (which covers gRPC), Npgsql and Redis instrumentation, all exported over OTLP to the Aspire dashboard. In compose, the standalone Aspire dashboard container plays the same role.
- **Business metrics** (`TicketHub.Booking`): holds succeeded, hold conflicts, confirmed, cancelled, expired, refunds requested. The messaging layer counts published, consumed, duplicate, retried and dead-lettered messages.

## Consequences

- One trace covers a whole purchase, including the asynchronous hops, which is where problems are hardest to locate.
- The outbox carries two extra small columns.
- With sampling, a trace is kept or dropped as a whole, because the sampled flag travels with the context.

## Tests

`Trace_context_flows_from_enqueue_to_handler` (in `TicketHub.BuildingBlocks.Tests`) asserts that the handler runs under the same `TraceId` as the request that enqueued the message.
