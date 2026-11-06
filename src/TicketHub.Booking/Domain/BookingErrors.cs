using TicketHub.BuildingBlocks.Results;

namespace TicketHub.Booking.Domain;

public static class BookingErrors
{
    public static Error NoSeats => Error.Validation("no-seats", "Select at least one seat.");
    public static Error DuplicateSeats => Error.Validation("duplicate-seats", "Each seat can only be selected once.");
    public static Error TooManySeats(int max) => Error.Validation("too-many-seats", $"A reservation can hold at most {max} seats.");
    public static Error MixedCurrencies => Error.Validation("mixed-currencies", "All seats must be priced in the same currency.");

    public static Error EventNotFound => Error.NotFound("event-not-found", "The event does not exist.");
    public static Error EventNotOnSale => Error.Unprocessable("event-not-on-sale", "Tickets for this event are not on sale.");
    public static Error ReservationNotFound => Error.NotFound("reservation-not-found", "The reservation does not exist.");

    public static Error SeatsNotFound(IEnumerable<Guid> seatIds) =>
        Error.NotFound("seat-not-found", "Some seats do not exist for this event.") with { Details = SeatDetails(seatIds) };

    public static Error SeatsNotSellable(IEnumerable<Guid> seatIds) =>
        Error.Unprocessable("seat-not-sellable", "Some seats cannot be sold for this event.") with { Details = SeatDetails(seatIds) };

    public static Error SeatsUnavailable(IEnumerable<Guid> seatIds) =>
        Error.Conflict("seat-unavailable", "Some seats were just taken by someone else.") with { Details = SeatDetails(seatIds) };

    public static Error SeatNotHeldByReservation => Error.Conflict("seat-not-held", "The seat is not held by this reservation.");

    public static Error CatalogUnavailable =>
        Error.Unavailable("catalog-unavailable", "Seat availability cannot be checked right now. Please retry shortly.");

    private static Dictionary<string, object?> SeatDetails(IEnumerable<Guid> seatIds) => new() { ["seatIds"] = seatIds.ToArray() };
}
