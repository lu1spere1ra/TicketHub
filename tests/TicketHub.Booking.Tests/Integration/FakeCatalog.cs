using System.Collections.Concurrent;
using TicketHub.Booking.Catalog;
using TicketHub.BuildingBlocks;

namespace TicketHub.Booking.Tests.Integration;

/// <summary>Stands in for Catalog's gRPC service: every known seat is sellable at 25 EUR unless told otherwise.</summary>
public sealed class FakeCatalog : ICatalogSeatChecker
{
    public ConcurrentDictionary<Guid, (bool OnSale, HashSet<Guid> Seats)> Events { get; } = new();
    public ConcurrentDictionary<Guid, bool> Unsellable { get; } = new();
    public bool Unavailable { get; set; }

    public (Guid EventId, Guid[] SeatIds) AddEvent(int seats = 10, bool onSale = true)
    {
        var eventId = Guid.NewGuid();
        var seatIds = Enumerable.Range(0, seats).Select(_ => Guid.NewGuid()).ToArray();
        Events[eventId] = (onSale, [.. seatIds]);
        return (eventId, seatIds);
    }

    public Task<SeatCheckResult> CheckAsync(Guid eventId, IReadOnlyCollection<Guid> seatIds, CancellationToken ct)
    {
        if (Unavailable)
        {
            throw new CatalogUnavailableException("fake outage", new InvalidOperationException());
        }

        if (!Events.TryGetValue(eventId, out var @event))
        {
            return Task.FromResult(new SeatCheckResult(false, false, []));
        }

        var seats = seatIds.Select(id => @event.Seats.Contains(id)
                ? new SeatCheck(id, true, !Unsellable.ContainsKey(id), new Money(25m, Money.Euro), $"F-1{id.ToString()[..4]}")
                : new SeatCheck(id, false, false, default, string.Empty))
            .ToList();
        return Task.FromResult(new SeatCheckResult(true, @event.OnSale, seats));
    }
}
