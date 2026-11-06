using TicketHub.BuildingBlocks;
using TicketHub.BuildingBlocks.Results;

namespace TicketHub.Booking.Domain;

public enum ReservationStatus
{
    Held,
    AwaitingPayment,
    Confirmed,
    Cancelled,
    Expired,
}

public sealed record CustomerRef(string UserId, string Email);

public sealed record ReservedSeat(Guid SeatId, string Label, Money Price)
{
    // For EF Core materialisation: a complex property (Price) cannot be bound to a constructor parameter.
    private ReservedSeat() : this(Guid.Empty, string.Empty, default) { }
}

/// <summary>One step in a reservation's life, shown by the admin saga endpoint.</summary>
public sealed record TimelineEntry(DateTimeOffset At, string Step, string? Detail = null)
{
    private TimelineEntry() : this(default, string.Empty) { }
}

public enum PaymentSucceededOutcome
{
    /// <summary>The reservation is now confirmed: sell the seats.</summary>
    Confirmed,

    /// <summary>Redelivery of a payment that was already applied: do nothing.</summary>
    Duplicate,

    /// <summary>The seats are no longer ours (expired or cancelled): the money must go back.</summary>
    RefundRequired,
}

/// <summary>
/// A customer's claim on one or more seats, from hold to confirmation. It is also the state of the checkout saga
/// that Booking orchestrates (ADR-0006): every transition below is one saga step, and each is recorded in
/// <see cref="Timeline"/>.
/// </summary>
public sealed class Reservation
{
    private readonly List<ReservedSeat> _seats = [];
    private readonly List<TimelineEntry> _timeline = [];

    private Reservation() { }

    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public string CustomerId { get; private set; } = string.Empty;
    public string CustomerEmail { get; private set; } = string.Empty;
    public ReservationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Absolute deadline: unpaid seats return to sale at this instant.</summary>
    public DateTimeOffset HoldExpiresAt { get; private set; }

    public Money Total { get; private set; }
    public IReadOnlyList<ReservedSeat> Seats => _seats;
    public IReadOnlyList<TimelineEntry> Timeline => _timeline;

    public Guid? PaymentId { get; private set; }
    public string? CancellationReason { get; private set; }

    /// <summary>Set when a payment arrived for seats we no longer hold and a refund was requested.</summary>
    public Guid? RefundedPaymentId { get; private set; }
    public DateTimeOffset? RefundedAt { get; private set; }

    /// <summary>Optimistic concurrency token, so the expiry job and payment handlers cannot overwrite each other.</summary>
    public long Version { get; private set; }

    /// <summary>Checks the shape of a seat selection before anything else is looked up.</summary>
    public static Result ValidateSelection(IReadOnlyCollection<Guid> seatIds, int maxSeats)
    {
        if (seatIds.Count == 0)
        {
            return BookingErrors.NoSeats;
        }

        if (seatIds.Distinct().Count() != seatIds.Count)
        {
            return BookingErrors.DuplicateSeats;
        }

        return seatIds.Count > maxSeats ? BookingErrors.TooManySeats(maxSeats) : Result.Success();
    }

    public static Result<Reservation> Hold(
        Guid id, Guid eventId, CustomerRef customer, IReadOnlyList<ReservedSeat> seats, DateTimeOffset now, TimeSpan holdDuration, int maxSeats)
    {
        var validation = ValidateSelection(seats.Select(s => s.SeatId).ToList(), maxSeats);
        if (!validation.IsSuccess)
        {
            return validation.Error;
        }

        var currency = seats[0].Price.Currency;
        if (seats.Any(s => s.Price.Currency != currency))
        {
            return BookingErrors.MixedCurrencies;
        }

        var reservation = new Reservation
        {
            Id = id,
            EventId = eventId,
            CustomerId = customer.UserId,
            CustomerEmail = customer.Email,
            Status = ReservationStatus.Held,
            CreatedAt = now,
            HoldExpiresAt = now + holdDuration,
            Total = seats.Aggregate(Money.Zero(currency), (total, seat) => total + seat.Price),
        };
        reservation._seats.AddRange(seats);
        reservation._timeline.Add(new TimelineEntry(now, "Held", $"{seats.Count} seat(s) until {reservation.HoldExpiresAt:O}"));
        return reservation;
    }

    public bool IsExpiredAt(DateTimeOffset now) => now >= HoldExpiresAt;

    /// <summary>The customer asks to pay. Only a live hold can be checked out.</summary>
    public Result BeginCheckout(DateTimeOffset now)
    {
        if (Status == ReservationStatus.Expired)
        {
            return BookingErrors.HoldExpired;
        }

        if (Status != ReservationStatus.Held)
        {
            return BookingErrors.InvalidTransition(Status, "check out");
        }

        if (IsExpiredAt(now))
        {
            return BookingErrors.HoldExpired;
        }

        Transition(ReservationStatus.AwaitingPayment, now, "PaymentRequested", $"{Total}");
        return Result.Success();
    }

    /// <summary>
    /// Payments charged the customer. If we still hold the seats the reservation is confirmed, even if the deadline
    /// has just passed: nobody else can have taken seats that were never released. If the seats were already
    /// released (expired or cancelled), the charge must be refunded.
    /// </summary>
    public PaymentSucceededOutcome ApplyPaymentSucceeded(Guid paymentId, DateTimeOffset now)
    {
        switch (Status)
        {
            case ReservationStatus.AwaitingPayment:
                PaymentId = paymentId;
                Transition(ReservationStatus.Confirmed, now, "Confirmed", $"payment {paymentId}");
                return PaymentSucceededOutcome.Confirmed;

            case ReservationStatus.Confirmed when PaymentId == paymentId:
                return PaymentSucceededOutcome.Duplicate;

            case ReservationStatus.Expired or ReservationStatus.Cancelled when RefundedPaymentId == paymentId:
                return PaymentSucceededOutcome.Duplicate;

            default:
                // Late payment for seats we no longer hold, or a second payment for a confirmed reservation.
                RefundedPaymentId = paymentId;
                _timeline.Add(new TimelineEntry(now, "RefundRequested", $"payment {paymentId} arrived while {Status}"));
                Version++;
                return PaymentSucceededOutcome.RefundRequired;
        }
    }

    /// <summary>Payment was declined: compensate by ending the reservation so its seats return to sale.</summary>
    /// <returns>False when the failure no longer matters (the reservation already ended).</returns>
    public bool ApplyPaymentFailed(string reason, DateTimeOffset now)
    {
        if (Status != ReservationStatus.AwaitingPayment)
        {
            return false;
        }

        CancellationReason = reason;
        Transition(ReservationStatus.Cancelled, now, "Cancelled", reason);
        return true;
    }

    /// <summary>The deadline passed without a confirmed payment: release the seats.</summary>
    public Result Expire(DateTimeOffset now)
    {
        if (Status is not (ReservationStatus.Held or ReservationStatus.AwaitingPayment))
        {
            return BookingErrors.InvalidTransition(Status, "expire");
        }

        if (!IsExpiredAt(now))
        {
            return BookingErrors.HoldStillActive;
        }

        Transition(ReservationStatus.Expired, now, "Expired");
        return Result.Success();
    }

    public void RecordRefund(Guid paymentId, DateTimeOffset now)
    {
        if (RefundedAt is not null)
        {
            return;
        }

        RefundedAt = now;
        _timeline.Add(new TimelineEntry(now, "Refunded", $"payment {paymentId}"));
        Version++;
    }

    private void Transition(ReservationStatus status, DateTimeOffset now, string step, string? detail = null)
    {
        Status = status;
        _timeline.Add(new TimelineEntry(now, step, detail));
        Version++;
    }
}
