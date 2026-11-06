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

/// <summary>A customer's claim on one or more seats, from hold to confirmation.</summary>
public sealed class Reservation
{
    private readonly List<ReservedSeat> _seats = [];

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
        return reservation;
    }
}
