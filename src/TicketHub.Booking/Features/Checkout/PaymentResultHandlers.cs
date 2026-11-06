using Microsoft.EntityFrameworkCore;
using TicketHub.Booking.Data;
using TicketHub.Booking.Domain;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Contracts.Booking;
using TicketHub.Contracts.Payments;

namespace TicketHub.Booking.Features.Checkout;

// Saga step 3. These run inside the inbox transaction (ADR-0003): a duplicate delivery is skipped before it gets
// here, and if the expiry job changes the reservation concurrently, SaveChanges throws a concurrency conflict, the
// message is retried, and the handler re-reads the new state.

public sealed class PaymentSucceededHandler(BookingDbContext db, IOutbox outbox, TimeProvider timeProvider)
    : IMessageHandler<PaymentSucceeded>
{
    public async Task HandleAsync(PaymentSucceeded message, MessageContext context, CancellationToken cancellationToken)
    {
        var reservation = await db.Reservations.SingleOrDefaultAsync(r => r.Id == message.ReservationId, cancellationToken)
            ?? throw new InvalidOperationException($"Payment {message.PaymentId} refers to unknown reservation {message.ReservationId}.");
        var now = timeProvider.GetUtcNow();

        switch (reservation.ApplyPaymentSucceeded(message.PaymentId, now))
        {
            case PaymentSucceededOutcome.Confirmed:
                await ReservationSeats.SellAsync(db, outbox, reservation, cancellationToken);
                outbox.Enqueue(new ReservationConfirmed(
                    reservation.Id, reservation.EventId, reservation.CustomerEmail, reservation.Seats.Select(s => s.Label).ToList(),
                    reservation.Total.Amount, reservation.Total.Currency, now), reservation.Id);
                BookingMetrics.Confirmed.Add(1);
                break;

            case PaymentSucceededOutcome.RefundRequired:
                // Compensation: the customer paid for seats that were already released.
                outbox.Enqueue(new RefundPayment(message.PaymentId, reservation.Id, $"Reservation was {reservation.Status} when payment succeeded"), reservation.Id);
                BookingMetrics.RefundsRequested.Add(1);
                break;

            case PaymentSucceededOutcome.Duplicate:
                break;
        }
    }
}

public sealed class PaymentFailedHandler(BookingDbContext db, IOutbox outbox, TimeProvider timeProvider)
    : IMessageHandler<PaymentFailed>
{
    public async Task HandleAsync(PaymentFailed message, MessageContext context, CancellationToken cancellationToken)
    {
        var reservation = await db.Reservations.SingleOrDefaultAsync(r => r.Id == message.ReservationId, cancellationToken)
            ?? throw new InvalidOperationException($"Payment {message.PaymentId} refers to unknown reservation {message.ReservationId}.");
        var now = timeProvider.GetUtcNow();

        // Compensation: a declined payment releases the seats.
        if (reservation.ApplyPaymentFailed(message.Reason, now))
        {
            await ReservationSeats.ReleaseAsync(db, outbox, reservation, cancellationToken);
            outbox.Enqueue(new ReservationCancelled(reservation.Id, reservation.EventId, reservation.CustomerEmail, message.Reason, now), reservation.Id);
            BookingMetrics.Cancelled.Add(1);
        }
    }
}

public sealed class PaymentRefundedHandler(BookingDbContext db, TimeProvider timeProvider) : IMessageHandler<PaymentRefunded>
{
    public async Task HandleAsync(PaymentRefunded message, MessageContext context, CancellationToken cancellationToken)
    {
        var reservation = await db.Reservations.SingleOrDefaultAsync(r => r.Id == message.ReservationId, cancellationToken);
        reservation?.RecordRefund(message.PaymentId, timeProvider.GetUtcNow());
    }
}
