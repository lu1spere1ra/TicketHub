using Microsoft.EntityFrameworkCore;
using TicketHub.BuildingBlocks;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Contracts.Payments;
using TicketHub.Payments.Data;
using TicketHub.Payments.Domain;
using TicketHub.Payments.Provider;

namespace TicketHub.Payments.Features;

/// <summary>
/// Charges the customer. A provider outage throws, which sends the message to the retry queue (ADR-0007): the
/// retry uses the same idempotency key (the reservation id), so it can never charge twice.
/// </summary>
public sealed class RequestPaymentHandler(PaymentsDbContext db, IPaymentProvider provider, IOutbox outbox, TimeProvider timeProvider)
    : IMessageHandler<RequestPayment>
{
    public async Task HandleAsync(RequestPayment message, MessageContext context, CancellationToken cancellationToken)
    {
        var existing = await db.Payments.SingleOrDefaultAsync(p => p.ReservationId == message.ReservationId, cancellationToken);
        if (existing is not null)
        {
            // Already decided (e.g. a second RequestPayment for the same reservation): repeat the outcome, do not charge.
            Announce(existing);
            return;
        }

        var amount = new Money(message.Amount, message.Currency);
        var now = timeProvider.GetUtcNow();
        var payment = await provider.ChargeAsync(new ChargeRequest(message.ReservationId.ToString(), amount, message.PaymentToken), cancellationToken) switch
        {
            ChargeResult.Approved approved => Payment.Succeeded(message.ReservationId, amount, approved.ProviderReference, now),
            ChargeResult.Declined declined => Payment.Declined(message.ReservationId, amount, declined.Reason, now),
            _ => throw new InvalidOperationException("Unknown charge result."),
        };

        db.Payments.Add(payment);
        Announce(payment);
    }

    private void Announce(Payment payment)
    {
        if (payment.Status == PaymentStatus.Declined)
        {
            outbox.Enqueue(new PaymentFailed(payment.Id, payment.ReservationId, payment.DeclineReason ?? "Declined", payment.CreatedAt), payment.ReservationId);
        }
        else
        {
            outbox.Enqueue(new PaymentSucceeded(payment.Id, payment.ReservationId, payment.Amount.Amount, payment.Amount.Currency, payment.CreatedAt), payment.ReservationId);
        }
    }
}

/// <summary>Compensation requested by Booking: the customer paid for seats that were already released.</summary>
public sealed class RefundPaymentHandler(PaymentsDbContext db, IPaymentProvider provider, IOutbox outbox, TimeProvider timeProvider)
    : IMessageHandler<RefundPayment>
{
    public async Task HandleAsync(RefundPayment message, MessageContext context, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == message.PaymentId, cancellationToken)
            ?? throw new InvalidOperationException($"Refund requested for unknown payment {message.PaymentId}.");

        var refund = payment.Refund(timeProvider.GetUtcNow());
        if (!refund.IsSuccess)
        {
            throw new InvalidOperationException(refund.Error.Message);
        }

        if (refund.Value)
        {
            await provider.RefundAsync(payment.ProviderReference!, cancellationToken);
            outbox.Enqueue(new PaymentRefunded(payment.Id, payment.ReservationId, payment.RefundedAt!.Value), payment.ReservationId);
        }
    }
}
