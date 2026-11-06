using Microsoft.EntityFrameworkCore;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Catalog.Data;
using TicketHub.Catalog.Domain;
using TicketHub.Contracts.Booking;

namespace TicketHub.Catalog.Features.SeatAvailability;

/// <summary>Keeps the seat-map projection in line with Booking, which owns seat allocation.</summary>
public sealed class SeatAvailabilityChangedHandler(CatalogDbContext db) : IMessageHandler<SeatAvailabilityChanged>
{
    public async Task HandleAsync(SeatAvailabilityChanged message, MessageContext context, CancellationToken cancellationToken)
    {
        var seatIds = message.Seats.Select(s => s.SeatId).ToList();
        var existing = await db.SeatStatuses
            .Where(s => s.EventId == message.EventId && seatIds.Contains(s.SeatId))
            .ToDictionaryAsync(s => s.SeatId, cancellationToken);

        foreach (var change in message.Seats)
        {
            var status = Enum.Parse<SeatStatus>(change.Status);
            if (existing.TryGetValue(change.SeatId, out var projected))
            {
                projected.Apply(status, change.Version);
            }
            else
            {
                db.SeatStatuses.Add(EventSeatStatus.Create(message.EventId, change.SeatId, status, change.Version));
            }
        }
    }
}
