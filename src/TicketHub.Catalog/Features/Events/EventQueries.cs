using Microsoft.EntityFrameworkCore;
using TicketHub.Catalog.Data;
using TicketHub.Catalog.Domain;

namespace TicketHub.Catalog.Features.Events;

/// <summary>Read side of the catalog. Plain EF queries projected straight to response records.</summary>
public sealed class EventQueries(CatalogDbContext db, TimeProvider timeProvider)
{
    public const int MaxPageSize = 100;

    public async Task<PagedResponse<EventSummary>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var now = timeProvider.GetUtcNow();

        var query = from e in db.Events.AsNoTracking()
                    join v in db.Venues.AsNoTracking() on e.VenueId equals v.Id
                    orderby e.StartsAt, e.Id
                    select new { e.Id, e.Name, Venue = v.Name, e.StartsAt, e.SalesOpenAt, e.SalesCloseAt };

        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return new PagedResponse<EventSummary>(
            items.Select(e => new EventSummary(e.Id, e.Name, e.Venue, e.StartsAt, now >= e.SalesOpenAt && now < e.SalesCloseAt)).ToList(),
            page, pageSize, total);
    }

    public async Task<EventDetails?> GetAsync(Guid eventId, CancellationToken ct)
    {
        var @event = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, ct);
        if (@event is null)
        {
            return null;
        }

        var venue = await db.Venues.AsNoTracking().Where(v => v.Id == @event.VenueId).Select(v => v.Name).SingleAsync(ct);
        return new EventDetails(
            @event.Id, @event.Name, @event.VenueId, venue, @event.StartsAt, @event.SalesOpenAt, @event.SalesCloseAt,
            @event.IsOnSale(timeProvider.GetUtcNow()),
            @event.Prices.Select(p => new SectionPriceResponse(p.Section, p.Amount, p.Currency)).ToList());
    }

    public async Task<SeatMapResponse?> GetSeatMapAsync(Guid eventId, CancellationToken ct)
    {
        var @event = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, ct);
        if (@event is null)
        {
            return null;
        }

        var seats = await db.Seats.AsNoTracking().Where(s => s.VenueId == @event.VenueId).ToListAsync(ct);
        var statuses = await db.SeatStatuses.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .ToDictionaryAsync(s => s.SeatId, s => s.Status, ct);

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
                    .Select(row => new SeatMapRow(
                        row.Key,
                        row.OrderBy(s => s.Number)
                            .Select(s => new SeatMapSeat(s.Id, s.Number, s.Label, StatusOf(s, statuses)))
                            .ToList()))
                    .ToList();
                return new SeatMapSection(section.Key, price.Amount, price.Currency, rows);
            })
            .ToList();

        return new SeatMapResponse(@event.Id, @event.Name, sections);
    }

    private static string StatusOf(Seat seat, Dictionary<Guid, SeatStatus> statuses) =>
        seat.IsBlocked ? "Blocked"
        : statuses.TryGetValue(seat.Id, out var status) ? status.ToString()
        : nameof(SeatStatus.Available);
}
