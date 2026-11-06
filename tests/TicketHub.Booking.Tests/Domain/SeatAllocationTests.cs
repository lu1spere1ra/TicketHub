using TicketHub.Booking.Domain;

namespace TicketHub.Booking.Tests.Domain;

public class SeatAllocationTests
{
    private static readonly Guid ReservationA = Guid.NewGuid();
    private static readonly Guid ReservationB = Guid.NewGuid();

    private static SeatAllocation NewSeat() => SeatAllocation.Create(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Holding_an_available_seat_records_the_reservation_and_bumps_the_version()
    {
        var seat = NewSeat();

        seat.Hold(ReservationA).IsSuccess.ShouldBeTrue();

        seat.Status.ShouldBe(AllocationStatus.Held);
        seat.ReservationId.ShouldBe(ReservationA);
        seat.Version.ShouldBe(1);
    }

    [Fact]
    public void Allocation_cannot_be_held_when_already_held_or_sold()
    {
        var held = NewSeat();
        held.Hold(ReservationA);
        var sold = NewSeat();
        sold.Hold(ReservationA);
        sold.Sell(ReservationA);

        held.Hold(ReservationB).Error!.Code.ShouldBe("seat-unavailable");
        sold.Hold(ReservationB).Error!.Code.ShouldBe("seat-unavailable");
        held.ReservationId.ShouldBe(ReservationA);
    }

    [Fact]
    public void Allocation_release_by_another_reservation_is_rejected()
    {
        var seat = NewSeat();
        seat.Hold(ReservationA);

        seat.Release(ReservationB).IsSuccess.ShouldBeFalse();

        seat.Status.ShouldBe(AllocationStatus.Held);
        seat.Version.ShouldBe(1);
    }

    [Fact]
    public void Released_seat_is_available_again_with_a_new_version()
    {
        var seat = NewSeat();
        seat.Hold(ReservationA);

        seat.Release(ReservationA).IsSuccess.ShouldBeTrue();

        seat.Status.ShouldBe(AllocationStatus.Available);
        seat.ReservationId.ShouldBeNull();
        seat.Version.ShouldBe(2);
    }

    [Fact]
    public void Sold_seat_cannot_be_released()
    {
        var seat = NewSeat();
        seat.Hold(ReservationA);
        seat.Sell(ReservationA);

        seat.Release(ReservationA).IsSuccess.ShouldBeFalse();
        seat.Status.ShouldBe(AllocationStatus.Sold);
    }

    [Fact]
    public void Only_the_holding_reservation_can_buy_the_seat()
    {
        var seat = NewSeat();
        seat.Hold(ReservationA);

        seat.Sell(ReservationB).IsSuccess.ShouldBeFalse();
        seat.Sell(ReservationA).IsSuccess.ShouldBeTrue();
        seat.Status.ShouldBe(AllocationStatus.Sold);
    }
}
