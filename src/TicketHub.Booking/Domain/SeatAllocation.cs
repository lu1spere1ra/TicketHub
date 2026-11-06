using TicketHub.BuildingBlocks.Results;

namespace TicketHub.Booking.Domain;

public enum AllocationStatus
{
    Available,
    Held,
    Sold,
}

/// <summary>
/// Who has a seat for an event. This is the only place in the system that decides whether a seat is free.
/// <para>
/// <see cref="Version"/> is an optimistic concurrency token: every change increments it, and the UPDATE only
/// succeeds if the row still has the version that was read. Of N concurrent claims for the same seat, exactly one
/// commits. The same number is published to consumers so they can order changes per seat (see ADR-0004).
/// </para>
/// </summary>
public sealed class SeatAllocation
{
    private SeatAllocation() { }

    public Guid EventId { get; private set; }
    public Guid SeatId { get; private set; }
    public AllocationStatus Status { get; private set; }
    public Guid? ReservationId { get; private set; }
    public long Version { get; private set; }

    public static SeatAllocation Create(Guid eventId, Guid seatId) =>
        new() { EventId = eventId, SeatId = seatId, Status = AllocationStatus.Available };

    public Result Hold(Guid reservationId)
    {
        if (Status != AllocationStatus.Available)
        {
            return BookingErrors.SeatsUnavailable([SeatId]);
        }

        Status = AllocationStatus.Held;
        ReservationId = reservationId;
        Version++;
        return Result.Success();
    }

    /// <summary>Returns a held seat to sale. Only the reservation holding it may release it; sold seats stay sold.</summary>
    public Result Release(Guid reservationId)
    {
        if (Status != AllocationStatus.Held || ReservationId != reservationId)
        {
            return BookingErrors.SeatNotHeldByReservation;
        }

        Status = AllocationStatus.Available;
        ReservationId = null;
        Version++;
        return Result.Success();
    }

    public Result Sell(Guid reservationId)
    {
        if (Status != AllocationStatus.Held || ReservationId != reservationId)
        {
            return BookingErrors.SeatNotHeldByReservation;
        }

        Status = AllocationStatus.Sold;
        Version++;
        return Result.Success();
    }
}
