# ADR-0009: Catalog caching: cache what is static, read what is live

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

Browsing traffic (the event list and seat maps) is much heavier than booking traffic. Most of the seat map never changes: the venue layout, labels and section prices. One part changes constantly: which seats are held or sold.

A common approach caches the whole seat map and invalidates it on every change. That creates races: an invalidation can run before the change commits, and a reader then re-caches the old value. It also floods the cache with writes during a sale.

## Decision

- **Cached in HybridCache:** the event list, event details, and the seat-map **layout** (sections, rows, seat IDs, labels, prices, blocked seats). HybridCache keeps an in-memory L1 (1 min) per replica and uses **Redis as the shared L2** (5 min), with stampede protection on misses.
- **Never cached:** seat status. Each seat-map request runs one indexed query on the event's projection rows and merges it with the cached layout.
- **No invalidation code at all.** Layout data only changes through the seed or migrations, and the TTL covers those.
- `OnSale` is computed per request from the cached sales window and the current time, so a cache entry never freezes a time-dependent answer.

## Consequences

- Seat maps cost one small query instead of three plus a layout join, and the event list costs nothing on a hit.
- Availability shown to customers is exactly as fresh as the projection. Its only lag is message delivery (ADR-0005), not cache TTL.
- If Redis is down, HybridCache falls back to L1 and the database. Redis is an optimisation, not a dependency of correctness, and `Catalog_keeps_serving_when_redis_is_down` pins this.
- Changing prices or layouts for an event that is on sale would need explicit invalidation. That is out of scope; when it is added, it belongs where the change is made.

## Alternatives considered

- **Cache the whole seat map, invalidate on `SeatAvailabilityChanged`:** races between invalidation and commit, stale availability, and cache churn during sales.
- **Write-through Redis hashes per event:** fast reads, but a second source of truth to keep consistent.
- **No cache:** fine at this scale, honestly. The brief asks for one, and caching the static part is the version worth defending.
