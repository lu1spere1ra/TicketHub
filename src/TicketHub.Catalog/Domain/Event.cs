using TicketHub.BuildingBlocks;

namespace TicketHub.Catalog.Domain;

/// <summary>A scheduled performance at a venue, with a sales window and a price per section.</summary>
public sealed class Event
{
    private readonly List<SectionPrice> _prices = [];

    private Event() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Guid VenueId { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset SalesOpenAt { get; private set; }
    public DateTimeOffset SalesCloseAt { get; private set; }
    public IReadOnlyList<SectionPrice> Prices => _prices;

    public static Event Schedule(
        Guid id, string name, Venue venue, DateTimeOffset startsAt, DateTimeOffset salesOpenAt, DateTimeOffset salesCloseAt,
        IEnumerable<SectionPrice> prices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (salesOpenAt >= salesCloseAt)
        {
            throw new ArgumentException("Sales must open before they close.", nameof(salesOpenAt));
        }

        if (salesCloseAt > startsAt)
        {
            throw new ArgumentException("Sales must close before the event starts.", nameof(salesCloseAt));
        }

        var @event = new Event
        {
            Id = id,
            Name = name,
            VenueId = venue.Id,
            StartsAt = startsAt,
            SalesOpenAt = salesOpenAt,
            SalesCloseAt = salesCloseAt,
        };
        @event._prices.AddRange(prices);
        return @event;
    }

    public bool IsOnSale(DateTimeOffset now) => now >= SalesOpenAt && now < SalesCloseAt;

    /// <summary>The price of a seat in the given section, or null when the section is not on sale for this event.</summary>
    public Money? PriceFor(string section) =>
        _prices.FirstOrDefault(p => p.Section == section) is { } price ? new Money(price.Amount, price.Currency) : null;
}

public sealed record SectionPrice(string Section, decimal Amount, string Currency);
