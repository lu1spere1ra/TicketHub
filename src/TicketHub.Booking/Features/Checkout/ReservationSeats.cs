using Microsoft.EntityFrameworkCore;
using TicketHub.Booking.Data;
using TicketHub.Booking.Domain;
using TicketHub.Booking.Features.Reservations;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Contracts.Booking;

namespace TicketHub.Booking.Features.Checkout;

/// <summary>Seat-side effects of a saga step: sell or release a reservation's seats and announce the change.</summary>
internal static class ReservationSeats
{
    public static async Task SellAsync(BookingDbContext db, IOutbox outbox, Reservation reservation, CancellationToken ct)
    {
        var allocations = await LoadAsync(db, reservation, ct);
        foreach (var allocation in allocations)
        {
            Ensure(allocation.Sell(reservation.Id).IsSuccess, allocation, reservation);
        }

        outbox.Enqueue(SeatChanges.ToMessage(reservation.EventId, allocations), reservation.Id);
    }

    public static async Task ReleaseAsync(BookingDbContext db, IOutbox outbox, Reservation reservation, CancellationToken ct)
    {
        var allocations = await LoadAsync(db, reservation, ct);
        foreach (var allocation in allocations)
        {
            Ensure(allocation.Release(reservation.Id).IsSuccess, allocation, reservation);
        }

        outbox.Enqueue(SeatChanges.ToMessage(reservation.EventId, allocations), reservation.Id);
    }

    private static Task<List<SeatAllocation>> LoadAsync(BookingDbContext db, Reservation reservation, CancellationToken ct) =>
        db.SeatAllocations.Where(a => a.EventId == reservation.EventId && a.ReservationId == reservation.Id).ToListAsync(ct);

    // A reservation's seats are only ever changed through the reservation, inside the same transaction; a seat that
    // refuses the change means an invariant is broken, which is a bug rather than a business outcome.
    private static void Ensure(bool ok, SeatAllocation allocation, Reservation reservation)
    {
        if (!ok)
        {
            throw new InvalidOperationException(
                $"Seat {allocation.SeatId} is {allocation.Status}; it cannot follow reservation {reservation.Id} ({reservation.Status}).");
        }
    }
}
