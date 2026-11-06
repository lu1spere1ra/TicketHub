using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using TicketHub.Catalog.Data;

namespace TicketHub.Catalog.Grpc;

public sealed class SeatSellabilityService(CatalogDbContext db, TimeProvider timeProvider) : SeatSellability.SeatSellabilityBase
{
    public override async Task<CheckSeatsResponse> CheckSeats(CheckSeatsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.EventId, out var eventId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "event_id must be a UUID."));
        }

        var @event = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, context.CancellationToken);
        if (@event is null)
        {
            return new CheckSeatsResponse { EventFound = false };
        }

        var requestedIds = request.SeatIds
            .Select(id => Guid.TryParse(id, out var seatId) ? seatId : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
        var seats = await db.Seats.AsNoTracking()
            .Where(s => s.VenueId == @event.VenueId && requestedIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id.ToString(), context.CancellationToken);

        var response = new CheckSeatsResponse { EventFound = true, EventOnSale = @event.IsOnSale(timeProvider.GetUtcNow()) };
        foreach (var seatId in request.SeatIds)
        {
            if (!Guid.TryParse(seatId, out var parsed) || !seats.TryGetValue(parsed.ToString(), out var seat))
            {
                response.Seats.Add(new SeatInfo { SeatId = seatId, Exists = false });
                continue;
            }

            var price = @event.PriceFor(seat.Section);
            response.Seats.Add(new SeatInfo
            {
                SeatId = seatId,
                Exists = true,
                Sellable = !seat.IsBlocked && price is not null,
                PriceMinor = price?.ToMinorUnits() ?? 0,
                Currency = price?.Currency ?? string.Empty,
                Label = seat.Label,
            });
        }

        return response;
    }
}
