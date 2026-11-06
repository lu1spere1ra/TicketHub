using TicketHub.Booking.Domain;
using TicketHub.BuildingBlocks;

namespace TicketHub.Booking.Tests.Domain;

public class ReservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly CustomerRef Alice = new("alice", "alice@example.com");

    private static ReservedSeat Seat(decimal price = 25m, string currency = Money.Euro) =>
        new(Guid.NewGuid(), "A-11", new Money(price, currency));

    private static Reservation Hold(params ReservedSeat[] seats) =>
        Reservation.Hold(Guid.NewGuid(), Guid.NewGuid(), Alice, seats, Now, TimeSpan.FromMinutes(10), maxSeats: 6).Value;

    [Fact]
    public void Hold_sets_expiry_ten_minutes_after_now()
    {
        var reservation = Hold(Seat());

        reservation.Status.ShouldBe(ReservationStatus.Held);
        reservation.HoldExpiresAt.ShouldBe(Now.AddMinutes(10));
    }

    [Fact]
    public void Hold_total_is_sum_of_seat_prices()
    {
        Hold(Seat(45m), Seat(30m), Seat(30m)).Total.ShouldBe(new Money(105m, Money.Euro));
    }

    [Fact]
    public void Hold_rejects_duplicate_seats()
    {
        var seat = Seat();

        Reservation.Hold(Guid.NewGuid(), Guid.NewGuid(), Alice, [seat, seat], Now, TimeSpan.FromMinutes(10), 6)
            .Error!.Code.ShouldBe("duplicate-seats");
    }

    [Fact]
    public void Hold_rejects_more_than_max_seats()
    {
        var seats = Enumerable.Range(0, 7).Select(_ => Seat()).ToList();

        Reservation.Hold(Guid.NewGuid(), Guid.NewGuid(), Alice, seats, Now, TimeSpan.FromMinutes(10), 6)
            .Error!.Code.ShouldBe("too-many-seats");
    }

    [Fact]
    public void Hold_rejects_an_empty_selection()
    {
        Reservation.ValidateSelection([], 6).Error!.Code.ShouldBe("no-seats");
    }

    [Fact]
    public void Hold_rejects_seats_priced_in_different_currencies()
    {
        Reservation.Hold(Guid.NewGuid(), Guid.NewGuid(), Alice, [Seat(currency: "EUR"), Seat(currency: "USD")], Now, TimeSpan.FromMinutes(10), 6)
            .Error!.Code.ShouldBe("mixed-currencies");
    }
}
