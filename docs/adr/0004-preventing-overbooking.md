# ADR-0004: Preventing overbooking with optimistic concurrency

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

When a popular event goes on sale, hundreds of customers request the same seats within milliseconds. Selling one seat twice is the worst failure this system can have. The brief allows optimistic concurrency (a row version), a distributed lock in Redis, or both.

## Decision

The guarantee comes from **PostgreSQL**, using an explicit version column on `SeatAllocation`.

```sql
UPDATE "SeatAllocations"
SET "Status" = 'Held', "ReservationId" = @r, "Version" = @v + 1
WHERE "EventId" = @e AND "SeatId" = @s AND "Version" = @v;   -- 0 rows => someone else won
```

1. N requests read the seat at version `v` and all see it as `Available`.
2. The first UPDATE takes the row lock. The others wait.
3. The first transaction commits. Each waiting UPDATE re-evaluates its `WHERE` against the committed row. The version is now `v+1`, so it matches **0 rows**, and EF Core raises `DbUpdateConcurrencyException`.
4. The losing handler clears its change tracker and retries once. It now reads the seat as `Held`, and the domain rejects the hold with `409 seat-unavailable`.

Further details:

- **Multi-seat holds are all-or-nothing.** All allocations, the reservation and the outbox row are written in one `SaveChanges` transaction.
- **No deadlocks between overlapping holds.** EF Core orders its UPDATE commands by primary key, so two holds on `{s1,s2}` and `{s2,s1}` lock the rows in the same order. Allocation rows are created lazily, and that `INSERT … ON CONFLICT DO NOTHING` is sorted too. Without the sort, concurrent first claims did deadlock (`40P01`); a test now pins this.
- **Why an explicit `Version` and not PostgreSQL's `xmin`:** the same number is published in `SeatAvailabilityChanged`, so consumers can order changes per seat. `xmin` is a transaction ID, not a per-row sequence, so it cannot serve that purpose.

**No Redis lock.** It would not add safety. A Redis lock without a fencing token is not a correctness mechanism: a process pause or a network partition can let two holders both believe they hold the lock (Kleppmann's critique of Redlock). The fencing token would have to be checked by the database anyway, and that check *is* the version column. Adding Redis would only change *where* contention is felt, at the cost of a new failure mode. Redis is used for what it is good at: caching the catalog (ADR-0009).

## Consequences

- Correctness depends on one well-understood mechanism, in one database, which `Parallel_holds_for_same_seat_produce_exactly_one_reservation` tests with 100 concurrent requests. The k6 run in the README covers 500.
- Under heavy contention for a single seat, the losers each cost one short transaction before they get their `409`. PostgreSQL handles this comfortably at this scale, as the k6 results show. If it ever did not, an admission filter in front of the database could shed load, but the database would still decide.
- Postgres connections are the limiting resource under a burst. Requests queue for the connection pool rather than failing.

## Alternatives considered

- **Pessimistic locking** (`SELECT … FOR UPDATE`): also correct. But it holds row locks across the round trip to the application, and it gives no version to publish.
- **Unique partial index on active holds:** correct and lock-free. But "Available" would then be the absence of a row, and there would be no version for consumers.
- **Redis lock as the guarantee:** rejected for the reasons above.

## Failure modes

| Failure | What happens |
|---|---|
| Two customers race for one seat | One `201`, one `409`, never two `201`s. |
| Overlapping multi-seat requests | One request wins all its seats. No partial holds and no deadlock `500`. |
| Retry also loses (third concurrent writer) | `409`. The client can refresh the seat map. |
