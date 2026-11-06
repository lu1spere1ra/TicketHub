namespace TicketHub.Catalog.Domain;

public enum SeatStatus
{
    Available,
    Held,
    Sold,
}

/// <summary>
/// Catalog's read-only projection of a seat's state for one event, fed by Booking's events. Booking is the
/// source of truth; this exists so the seat map can be served (and cached) without calling Booking.
/// A missing row means the seat has never been claimed, i.e. it is available.
/// </summary>
public sealed class EventSeatStatus
{
    private EventSeatStatus() { }

    public Guid EventId { get; private set; }
    public Guid SeatId { get; private set; }
    public SeatStatus Status { get; private set; }

    /// <summary>Booking's per-seat version of the change this row reflects.</summary>
    public long Version { get; private set; }

    public static EventSeatStatus Create(Guid eventId, Guid seatId, SeatStatus status, long version) =>
        new() { EventId = eventId, SeatId = seatId, Status = status, Version = version };

    /// <summary>
    /// Applies a change unless it is older than (or the same as) what is already projected. Messages can arrive
    /// out of order, so the version, not arrival order, decides.
    /// </summary>
    /// <returns>True when the projection changed.</returns>
    public bool Apply(SeatStatus status, long version)
    {
        if (version <= Version)
        {
            return false;
        }

        Status = status;
        Version = version;
        return true;
    }
}
