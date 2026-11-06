using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TicketHub.Booking.Data;
using TicketHub.BuildingBlocks;
using TicketHub.BuildingBlocks.Http;
using TicketHub.BuildingBlocks.Results;

namespace TicketHub.Booking.Features.Reservations;

public sealed record HoldSeatsRequest(Guid EventId, IReadOnlyList<Guid> SeatIds);

public static class ReservationEndpoints
{
    public static IEndpointRouteBuilder MapReservationEndpoints(this IEndpointRouteBuilder app)
    {
        var reservations = app.MapGroup("/reservations").WithTags("Reservations").AddEndpointFilter<RequireCustomerFilter>();

        reservations.MapPost("/", async Task<IResult> (
                HoldSeatsRequest request, HoldSeatsHandler handler, ICurrentUser user, HttpContext http, CancellationToken ct) =>
            {
                var result = await handler.HandleAsync(new HoldSeatsCommand(request.EventId, request.SeatIds ?? []), user, ct);
                if (result.IsSuccess)
                {
                    return TypedResults.Created($"/reservations/{result.Value.Id}", result.Value);
                }

                if (result.Error.Kind == ErrorKind.Unavailable)
                {
                    // Roughly how long the circuit stays open before Catalog is tried again.
                    http.Response.Headers.RetryAfter = "15";
                }

                return result.Error.ToProblem();
            })
            .WithName("HoldSeats");

        reservations.MapGet("/{reservationId:guid}", async Task<Results<Ok<ReservationResponse>, NotFound>> (
                Guid reservationId, BookingDbContext db, ICurrentUser user, CancellationToken ct) =>
            {
                // Someone else's reservation is reported as missing, so ids cannot be probed.
                var reservation = await db.Reservations.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.Id == reservationId && r.CustomerId == user.UserId, ct);
                return reservation is null ? TypedResults.NotFound() : TypedResults.Ok(ReservationResponse.From(reservation));
            })
            .WithName("GetReservation");

        return reservations;
    }
}
