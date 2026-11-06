using TicketHub.BuildingBlocks;
using TicketHub.BuildingBlocks.Results;

namespace TicketHub.Payments.Domain;

public enum PaymentStatus
{
    Succeeded,
    Declined,
    Refunded,
}

/// <summary>The outcome of charging a customer for one reservation. At most one payment exists per reservation.</summary>
public sealed class Payment
{
    private Payment() { }

    public Guid Id { get; private set; }
    public Guid ReservationId { get; private set; }
    public Money Amount { get; private set; }
    public PaymentStatus Status { get; private set; }
    public string? ProviderReference { get; private set; }
    public string? DeclineReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RefundedAt { get; private set; }

    public static Payment Succeeded(Guid reservationId, Money amount, string providerReference, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        ReservationId = reservationId,
        Amount = amount,
        Status = PaymentStatus.Succeeded,
        ProviderReference = providerReference,
        CreatedAt = now,
    };

    public static Payment Declined(Guid reservationId, Money amount, string reason, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        ReservationId = reservationId,
        Amount = amount,
        Status = PaymentStatus.Declined,
        DeclineReason = reason,
        CreatedAt = now,
    };

    /// <returns>True when the refund must be executed now; false when it already was (idempotent).</returns>
    public Result<bool> Refund(DateTimeOffset now)
    {
        switch (Status)
        {
            case PaymentStatus.Refunded:
                return false;
            case PaymentStatus.Succeeded:
                Status = PaymentStatus.Refunded;
                RefundedAt = now;
                return true;
            default:
                return Error.Conflict("not-refundable", $"A {Status} payment cannot be refunded.");
        }
    }
}
