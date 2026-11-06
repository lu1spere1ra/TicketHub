using Microsoft.EntityFrameworkCore;
using TicketHub.Booking.Data;
using TicketHub.Booking.Domain;
using TicketHub.Booking.Features.Reservations;
using TicketHub.BuildingBlocks;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.BuildingBlocks.Results;
using TicketHub.Contracts.Payments;

namespace TicketHub.Booking.Features.Checkout;

public sealed record CheckoutCommand(Guid ReservationId, string PaymentToken);

/// <summary>Saga step 2: the customer pays. The charge itself happens asynchronously in Payments.</summary>
public sealed class CheckoutHandler(BookingDbContext db, IOutbox outbox, TimeProvider timeProvider)
{
    public async Task<Result<ReservationResponse>> HandleAsync(CheckoutCommand command, ICurrentUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.PaymentToken))
        {
            return Error.Validation("payment-token-required", "A payment token is required.");
        }

        var reservation = await db.Reservations.SingleOrDefaultAsync(r => r.Id == command.ReservationId && r.CustomerId == user.UserId, ct);
        if (reservation is null)
        {
            return BookingErrors.ReservationNotFound;
        }

        var checkout = reservation.BeginCheckout(timeProvider.GetUtcNow());
        if (!checkout.IsSuccess)
        {
            return checkout.Error;
        }

        outbox.Enqueue(
            new RequestPayment(reservation.Id, reservation.Total.Amount, reservation.Total.Currency, command.PaymentToken),
            reservation.Id);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The expiry job (or a concurrent checkout) changed the reservation first.
            return BookingErrors.InvalidTransition(reservation.Status, "check out");
        }

        return ReservationResponse.From(reservation);
    }
}
