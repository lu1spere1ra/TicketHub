using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketHub.Booking.Catalog;
using TicketHub.Booking.Data;
using TicketHub.Booking.Domain;
using TicketHub.BuildingBlocks;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.BuildingBlocks.Results;

namespace TicketHub.Booking.Features.Reservations;

public sealed record HoldSeatsCommand(Guid EventId, IReadOnlyList<Guid> SeatIds);

/// <summary>
/// Claims seats for a customer. This is the contended path; the steps are:
/// <list type="number">
/// <item>Validate the selection, then ask Catalog (gRPC) whether the seats can be sold and at what price.</item>
/// <item>Make sure an allocation row exists for every seat (rows are created on first touch).</item>
/// <item>In one transaction: hold every seat, create the reservation, enqueue <c>SeatAvailabilityChanged</c>.</item>
/// </list>
/// Two customers racing for a seat both read version N; the first UPDATE ... WHERE version = N wins and the other
/// affects no rows, which EF reports as a concurrency conflict. The loser retries once against fresh state, sees the
/// seat held, and gets a 409.
/// </summary>
public sealed class HoldSeatsHandler(
    BookingDbContext db,
    ICatalogSeatChecker catalog,
    IOutbox outbox,
    TimeProvider timeProvider,
    IOptions<HoldOptions> options)
{
    private const int ConcurrencyRetries = 1;

    public async Task<Result<ReservationResponse>> HandleAsync(HoldSeatsCommand command, ICurrentUser user, CancellationToken ct)
    {
        var holdOptions = options.Value;
        var selection = Reservation.ValidateSelection(command.SeatIds, holdOptions.MaxSeats);
        if (!selection.IsSuccess)
        {
            return selection.Error;
        }

        SeatCheckResult check;
        try
        {
            check = await catalog.CheckAsync(command.EventId, command.SeatIds, ct);
        }
        catch (CatalogUnavailableException)
        {
            return BookingErrors.CatalogUnavailable;
        }

        var sellability = EnsureSellable(check);
        if (!sellability.IsSuccess)
        {
            return sellability.Error;
        }

        await EnsureAllocationRowsExistAsync(command.EventId, command.SeatIds, ct);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TryHoldAsync(command, user, check, holdOptions, ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < ConcurrencyRetries)
            {
                db.ChangeTracker.Clear();
            }
            catch (DbUpdateConcurrencyException)
            {
                return BookingErrors.SeatsUnavailable(command.SeatIds);
            }
        }
    }

    private async Task<Result<ReservationResponse>> TryHoldAsync(
        HoldSeatsCommand command, ICurrentUser user, SeatCheckResult check, HoldOptions holdOptions, CancellationToken ct)
    {
        var allocations = await db.SeatAllocations
            .Where(a => a.EventId == command.EventId && command.SeatIds.Contains(a.SeatId))
            .ToListAsync(ct);

        var reservationId = Guid.CreateVersion7();
        var unavailable = allocations.Where(a => !a.Hold(reservationId).IsSuccess).Select(a => a.SeatId).ToList();
        if (unavailable.Count > 0)
        {
            return BookingErrors.SeatsUnavailable(unavailable);
        }

        var seats = check.Seats.Select(s => new ReservedSeat(s.SeatId, s.Label, s.Price)).ToList();
        var reservation = Reservation.Hold(
            reservationId, command.EventId, new CustomerRef(user.UserId, user.Email), seats,
            timeProvider.GetUtcNow(), holdOptions.Duration, holdOptions.MaxSeats);
        if (!reservation.IsSuccess)
        {
            return reservation.Error;
        }

        db.Reservations.Add(reservation.Value);
        outbox.Enqueue(SeatChanges.ToMessage(command.EventId, allocations), correlationId: reservationId);

        // EF orders the UPDATEs by primary key, so two overlapping holds always lock seats in the same order and
        // cannot deadlock: the second one waits, then finds the version changed.
        await db.SaveChangesAsync(ct);
        return ReservationResponse.From(reservation.Value);
    }

    private static Result EnsureSellable(SeatCheckResult check)
    {
        if (!check.EventFound)
        {
            return BookingErrors.EventNotFound;
        }

        var missing = check.Seats.Where(s => !s.Exists).Select(s => s.SeatId).ToList();
        if (missing.Count > 0)
        {
            return BookingErrors.SeatsNotFound(missing);
        }

        if (!check.EventOnSale)
        {
            return BookingErrors.EventNotOnSale;
        }

        var notSellable = check.Seats.Where(s => !s.Sellable).Select(s => s.SeatId).ToList();
        return notSellable.Count > 0 ? BookingErrors.SeatsNotSellable(notSellable) : Result.Success();
    }

    /// <summary>
    /// Booking only tracks seats someone has tried to claim; a seat without a row has never been claimed. Rows are
    /// inserted idempotently in their own statement, so concurrent first claims do not conflict here.
    /// Rows are inserted in a fixed (sorted) order: two requests inserting overlapping seats in different orders
    /// would otherwise wait on each other's uncommitted rows and deadlock.
    /// </summary>
    private Task EnsureAllocationRowsExistAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken ct)
    {
        var ids = seatIds.ToArray();
        var available = nameof(AllocationStatus.Available);
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO "SeatAllocations" ("EventId", "SeatId", "Status", "Version")
            SELECT {eventId}, seat_id, {available}, 0 FROM unnest({ids}) AS seat_id
            ORDER BY seat_id
            ON CONFLICT DO NOTHING
            """, ct);
    }
}
