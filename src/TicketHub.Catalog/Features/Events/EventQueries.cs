using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TicketHub.Catalog.Data;
using TicketHub.Catalog.Domain;

namespace TicketHub.Catalog.Features.Events;

/// <summary>
/// Read side of the catalog (ADR-0009). What rarely changes (events, venue layout, prices) is cached in
/// HybridCache: in memory, plus Redis shared between replicas. What changes every second (seat status) is always
/// read fresh with one indexed query. That way the cache never needs invalidating and never shows stale
/// availability.
/// </summary>
public sealed class EventQueries(CatalogDbContext db, HybridCache cache, TimeProvider timeProvider)
{
    public const int MaxPageSize = 100;

    private static readonly HybridCacheEntryOptions StaticData = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(1),
    };

    public async Task<PagedResponse<EventSummary>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var now = timeProvider.GetUtcNow();

        var listing = await cache.GetOrCreateAsync(
            $"events:list:{page}:{pageSize}",
            async token =>
            {
                var query = from e in db.Events.AsNoTracking()
                            join v in db.Venues.AsNoTracking() on e.VenueId equals v.Id
                            orderby e.StartsAt, e.Id
                            select new CachedEventSummary(e.Id, e.Name, v.Name, e.StartsAt, e.SalesOpenAt, e.SalesCloseAt);
                return new CachedEventPage(await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(token), await query.CountAsync(token));
            },
            StaticData,
            cancellationToken: ct);

        return new PagedResponse<EventSummary>(
            listing.Items.Select(e => new EventSummary(e.Id, e.Name, e.Venue, e.StartsAt, now >= e.SalesOpenAt && now < e.SalesCloseAt)).ToList(),
            page, pageSize, listing.TotalCount);
    }

    public async Task<EventDetails?> GetAsync(Guid eventId, CancellationToken ct)
    {
        var cached = await cache.GetOrCreateAsync(
            $"events:{eventId}",
            async token =>
            {
                var @event = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, token);
                if (@event is null)
                {
                    return null;
                }

                var venue = await db.Venues.AsNoTracking().Where(v => v.Id == @event.VenueId).Select(v => v.Name).SingleAsync(token);
                return new EventDetails(
                    @event.Id, @event.Name, @event.VenueId, venue, @event.StartsAt, @event.SalesOpenAt, @event.SalesCloseAt,
                    OnSale: false,
                    @event.Prices.Select(p => new SectionPriceResponse(p.Section, p.Amount, p.Currency)).ToList());
            },
            StaticData,
            cancellationToken: ct);

        var now = timeProvider.GetUtcNow();
        return cached is null ? null : cached with { OnSale = now >= cached.SalesOpenAt && now < cached.SalesCloseAt };
    }

    public async Task<SeatMapResponse?> GetSeatMapAsync(Guid eventId, CancellationToken ct)
    {
        var layout = await cache.GetOrCreateAsync($"events:{eventId}:layout", token => LoadLayoutAsync(eventId, token), StaticData, cancellationToken: ct);
        if (layout is null)
        {
            return null;
        }

        var statuses = await db.SeatStatuses.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .ToDictionaryAsync(s => s.SeatId, s => s.Status, ct);

        return new SeatMapResponse(
            layout.EventId,
            layout.EventName,
            layout.Sections.Select(section => new SeatMapSection(
                section.Name, section.Price, section.Currency,
                section.Rows.Select(row => new SeatMapRow(
                    row.Row,
                    row.Seats.Select(seat => new SeatMapSeat(seat.SeatId, seat.Number, seat.Label, StatusOf(seat, statuses))).ToList())).ToList())).ToList());
    }

    private async ValueTask<SeatMapLayout?> LoadLayoutAsync(Guid eventId, CancellationToken ct)
    {
        var @event = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, ct);
        if (@event is null)
        {
            return null;
        }

        var seats = await db.Seats.AsNoTracking().Where(s => s.VenueId == @event.VenueId).ToListAsync(ct);
        var sections = seats
            .Where(s => @event.PriceFor(s.Section) is not null)
            .GroupBy(s => s.Section)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(section =>
            {
                var price = @event.PriceFor(section.Key)!.Value;
                var rows = section
                    .GroupBy(s => s.Row)
                    .OrderBy(r => int.TryParse(r.Key, out var n) ? n : int.MaxValue).ThenBy(r => r.Key, StringComparer.Ordinal)
                    .Select(row => new LayoutRow(row.Key, row.OrderBy(s => s.Number).Select(s => new LayoutSeat(s.Id, s.Number, s.Label, s.IsBlocked)).ToList()))
                    .ToList();
                return new LayoutSection(section.Key, price.Amount, price.Currency, rows);
            })
            .ToList();

        return new SeatMapLayout(@event.Id, @event.Name, sections);
    }

    private static string StatusOf(LayoutSeat seat, Dictionary<Guid, SeatStatus> statuses) =>
        seat.IsBlocked ? "Blocked"
        : statuses.TryGetValue(seat.SeatId, out var status) ? status.ToString()
        : nameof(SeatStatus.Available);

    // Cache payloads: plain records, serialised to Redis as JSON.
    public sealed record CachedEventSummary(Guid Id, string Name, string Venue, DateTimeOffset StartsAt, DateTimeOffset SalesOpenAt, DateTimeOffset SalesCloseAt);
    public sealed record CachedEventPage(IReadOnlyList<CachedEventSummary> Items, int TotalCount);
    public sealed record SeatMapLayout(Guid EventId, string EventName, IReadOnlyList<LayoutSection> Sections);
    public sealed record LayoutSection(string Name, decimal Price, string Currency, IReadOnlyList<LayoutRow> Rows);
    public sealed record LayoutRow(string Row, IReadOnlyList<LayoutSeat> Seats);
    public sealed record LayoutSeat(Guid SeatId, int Number, string Label, bool IsBlocked);
}
