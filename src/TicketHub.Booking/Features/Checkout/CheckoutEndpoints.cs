using TicketHub.BuildingBlocks;
using TicketHub.BuildingBlocks.Http;

namespace TicketHub.Booking.Features.Checkout;

public sealed record CheckoutRequest(string PaymentToken);

public static class CheckoutEndpoints
{
    public static IEndpointRouteBuilder MapCheckoutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/reservations/{reservationId:guid}/checkout", async Task<IResult> (
                Guid reservationId, CheckoutRequest request, CheckoutHandler handler, ICurrentUser user, CancellationToken ct) =>
            {
                var result = await handler.HandleAsync(new CheckoutCommand(reservationId, request.PaymentToken), user, ct);
                // 202: the payment is processed asynchronously; poll the reservation for the outcome.
                return result.IsSuccess
                    ? TypedResults.Accepted($"/reservations/{reservationId}", result.Value)
                    : result.Error.ToProblem();
            })
            .WithTags("Reservations")
            .WithName("Checkout")
            .RequireAuthorization(AuthenticationExtensions.CustomerPolicy);

        return app;
    }
}
