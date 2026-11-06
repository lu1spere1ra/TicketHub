namespace TicketHub.Catalog.Features.Events;

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record EventSummary(Guid Id, string Name, string Venue, DateTimeOffset StartsAt, bool OnSale);

public sealed record EventDetails(
    Guid Id, string Name, Guid VenueId, string Venue, DateTimeOffset StartsAt, DateTimeOffset SalesOpenAt,
    DateTimeOffset SalesCloseAt, bool OnSale, IReadOnlyList<SectionPriceResponse> Prices);

public sealed record SectionPriceResponse(string Section, decimal Amount, string Currency);

public sealed record SeatMapResponse(Guid EventId, string EventName, IReadOnlyList<SeatMapSection> Sections);

public sealed record SeatMapSection(string Name, decimal Price, string Currency, IReadOnlyList<SeatMapRow> Rows);

public sealed record SeatMapRow(string Row, IReadOnlyList<SeatMapSeat> Seats);

/// <param name="Status"><c>Available</c>, <c>Held</c>, <c>Sold</c> or <c>Blocked</c>.</param>
public sealed record SeatMapSeat(Guid SeatId, int Number, string Label, string Status);
