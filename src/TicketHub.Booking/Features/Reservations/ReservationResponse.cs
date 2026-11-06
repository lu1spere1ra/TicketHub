using TicketHub.Booking.Domain;

namespace TicketHub.Booking.Features.Reservations;

public sealed record ReservationResponse(
    Guid Id, Guid EventId, string Status, DateTimeOffset HoldExpiresAt, decimal Total, string Currency,
    IReadOnlyList<ReservedSeatResponse> Seats)
{
    public static ReservationResponse From(Reservation reservation) => new(
        reservation.Id,
        reservation.EventId,
        reservation.Status.ToString(),
        reservation.HoldExpiresAt,
        reservation.Total.Amount,
        reservation.Total.Currency,
        reservation.Seats.Select(s => new ReservedSeatResponse(s.SeatId, s.Label, s.Price.Amount)).ToList());
}

public sealed record ReservedSeatResponse(Guid SeatId, string Label, decimal Price);
