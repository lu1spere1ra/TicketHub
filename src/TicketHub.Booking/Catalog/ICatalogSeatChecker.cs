using Grpc.Core;
using TicketHub.BuildingBlocks;
using TicketHub.Catalog.Grpc;

namespace TicketHub.Booking.Catalog;

public sealed record SeatCheck(Guid SeatId, bool Exists, bool Sellable, Money Price, string Label);

public sealed record SeatCheckResult(bool EventFound, bool EventOnSale, IReadOnlyList<SeatCheck> Seats);

/// <summary>Booking's view of Catalog: can these seats be sold, and at what price.</summary>
public interface ICatalogSeatChecker
{
    /// <exception cref="CatalogUnavailableException">Catalog could not answer.</exception>
    Task<SeatCheckResult> CheckAsync(Guid eventId, IReadOnlyCollection<Guid> seatIds, CancellationToken ct);
}

public sealed class CatalogUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>Adapter over the generated gRPC client; keeps protobuf types out of the booking code.</summary>
internal sealed class GrpcCatalogSeatChecker(SeatSellability.SeatSellabilityClient client) : ICatalogSeatChecker
{
    public async Task<SeatCheckResult> CheckAsync(Guid eventId, IReadOnlyCollection<Guid> seatIds, CancellationToken ct)
    {
        var request = new CheckSeatsRequest { EventId = eventId.ToString() };
        request.SeatIds.AddRange(seatIds.Select(id => id.ToString()));

        CheckSeatsResponse response;
        try
        {
            response = await client.CheckSeatsAsync(request, cancellationToken: ct);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Internal or StatusCode.Unknown)
        {
            throw new CatalogUnavailableException("Catalog did not answer CheckSeats.", ex);
        }

        return new SeatCheckResult(
            response.EventFound,
            response.EventOnSale,
            response.Seats.Select(s => new SeatCheck(
                Guid.Parse(s.SeatId), s.Exists, s.Sellable, Money.FromMinorUnits(s.PriceMinor, s.Currency), s.Label)).ToList());
    }
}
