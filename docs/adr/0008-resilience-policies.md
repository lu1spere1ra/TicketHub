# ADR-0008: Resilience policies

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

Resilience is often applied as "add a retry handler to every HttpClient". Retries are only safe when the operation is idempotent, they multiply load on a struggling dependency, and they stack when several layers retry. Each call was therefore looked at individually.

## Decision

| Call | Policy | Why |
|---|---|---|
| Booking → Catalog `CheckSeats` (gRPC) | gRPC retry policy (Unavailable, 2 retries, 100 ms → 1 s backoff). HTTP circuit breaker (opens at ≥ 50 % failures over 30 s, minimum 10 calls; stays open 15 s). 1 s per-attempt timeout. 3 s call deadline. | The only synchronous dependency on the hot path. It is a read, so retrying is safe. |
| Gateway → services (YARP) | **No retries.** | A replayed `POST /reservations` could hold seats twice. Clients decide whether to retry. |
| Message handlers (Payments → provider, Notifications → SMTP, saga steps) | **Message-level retries only:** 5 attempts, 5 s apart, then the dead-letter queue (ADR-0007). | Retrying inside a handler would hold the message, the database transaction and the connection while waiting. The broker already gives durable, bounded retries. |

### Why gRPC retries are not done with Http.Resilience

The obvious approach, `AddStandardResilienceHandler()` on the gRPC client, **hangs on the first retry**. gRPC writes the request body as a stream that can be read only once, so an HTTP-level retry re-sends a request with no body and waits forever. The hang was found by a test, not by reading the docs.

Retries therefore belong to the gRPC channel (`ServiceConfig.RetryPolicy`), which buffers the message and replays it properly. `Microsoft.Extensions.Http.Resilience` still wraps every HTTP attempt, with a circuit breaker and an attempt timeout, so the breaker counts each retry individually.

### gRPC status codes at the HTTP layer

A failed gRPC call is often an HTTP 200 with `grpc-status` in the trailers, which an HTTP handler cannot see. When the server fails before sending a body, it answers "trailers-only", and then `grpc-status` arrives in the response headers. The breaker's predicate reads that header: status 14 (Unavailable) and 4 (DeadlineExceeded) count as failures, alongside HTTP 5xx, 408, 429, connection errors and timeouts.

### What the caller sees

When the circuit is open, or the deadline expires, `POST /reservations` returns `503` with `Retry-After: 15`, the break duration. Requests do not pile up waiting for a dead dependency.

## Consequences

- A Catalog outage costs at most about 3 s per hold until the breaker opens; after that, holds fail in milliseconds. When Catalog recovers, the breaker's half-open probe lets traffic back in.
- The retry layers multiply, but only in one place and with a known bound: at most 3 attempts per hold, inside a 3 s deadline.
- The breaker counts per Booking replica, not across the cluster. That is the usual trade-off for client-side breakers.

## Alternatives considered

- **The standard resilience handler with defaults:** wrong for gRPC (the hang above) and too generous for a user-facing path (the default total timeout is 30 s).
- **A shared breaker in Redis:** coordinates replicas, but adds a dependency to the very path it is meant to protect.

## Tests

`CatalogResilienceTests` runs the real gRPC client against a stub HTTP/2 server:

- Trailers-only Unavailable is retried exactly twice.
- An open circuit answers ten holds in under a second without reaching Catalog.
- Holds recover once Catalog does.
