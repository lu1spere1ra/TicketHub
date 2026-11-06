using TicketHub.Booking.Domain;
using TicketHub.Contracts.Booking;

namespace TicketHub.Booking.Features.Reservations;

internal static class SeatChanges
{
    public static SeatAvailabilityChanged ToMessage(Guid eventId, IEnumerable<SeatAllocation> allocations) =>
        new(eventId, allocations.Select(a => new SeatStatusChange(a.SeatId, a.Status.ToString(), a.Version)).ToList());
}
