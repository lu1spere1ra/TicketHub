using TicketHub.Booking.Domain;
using TicketHub.BuildingBlocks;

namespace TicketHub.Booking.Tests.Domain;

public class ReservationSagaTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterDeadline = Now.AddMinutes(10);
    private static readonly Guid PaymentId = Guid.NewGuid();

    private static Reservation Held() => Reservation.Hold(
        Guid.NewGuid(), Guid.NewGuid(), new CustomerRef("alice", "alice@example.com"),
        [new ReservedSeat(Guid.NewGuid(), "A-11", new Money(25m, Money.Euro))], Now, TimeSpan.FromMinutes(10), 6).Value;

    private static Reservation AwaitingPayment()
    {
        var reservation = Held();
        reservation.BeginCheckout(Now.AddMinutes(1));
        return reservation;
    }

    [Fact]
    public void Checkout_moves_a_live_hold_to_awaiting_payment()
    {
        var reservation = Held();

        reservation.BeginCheckout(Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        reservation.Status.ShouldBe(ReservationStatus.AwaitingPayment);
    }

    [Fact]
    public void Checkout_after_deadline_returns_hold_expired()
    {
        var reservation = Held();

        reservation.BeginCheckout(AfterDeadline).Error!.Code.ShouldBe("hold-expired");
        reservation.Status.ShouldBe(ReservationStatus.Held);
    }

    [Fact]
    public void Checkout_of_an_expired_reservation_returns_hold_expired()
    {
        var reservation = Held();
        reservation.Expire(AfterDeadline);

        reservation.BeginCheckout(AfterDeadline).Error!.Code.ShouldBe("hold-expired");
    }

    [Fact]
    public void Checkout_twice_is_rejected()
    {
        AwaitingPayment().BeginCheckout(Now.AddMinutes(2)).Error!.Code.ShouldBe("invalid-state");
    }

    [Fact]
    public void Payment_success_confirms_a_reservation_awaiting_payment()
    {
        var reservation = AwaitingPayment();

        reservation.ApplyPaymentSucceeded(PaymentId, Now.AddMinutes(2)).ShouldBe(PaymentSucceededOutcome.Confirmed);

        reservation.Status.ShouldBe(ReservationStatus.Confirmed);
        reservation.PaymentId.ShouldBe(PaymentId);
    }

    [Fact]
    public void Payment_success_after_deadline_still_confirms_while_seats_were_not_released()
    {
        var reservation = AwaitingPayment();

        reservation.ApplyPaymentSucceeded(PaymentId, AfterDeadline.AddSeconds(5)).ShouldBe(PaymentSucceededOutcome.Confirmed);
    }

    [Fact]
    public void Payment_success_after_expiry_requires_refund()
    {
        var reservation = AwaitingPayment();
        reservation.Expire(AfterDeadline);

        reservation.ApplyPaymentSucceeded(PaymentId, AfterDeadline.AddSeconds(5)).ShouldBe(PaymentSucceededOutcome.RefundRequired);

        reservation.Status.ShouldBe(ReservationStatus.Expired);
        reservation.RefundedPaymentId.ShouldBe(PaymentId);
    }

    [Fact]
    public void Duplicate_payment_success_is_idempotent()
    {
        var confirmed = AwaitingPayment();
        confirmed.ApplyPaymentSucceeded(PaymentId, Now.AddMinutes(2));
        var expired = AwaitingPayment();
        expired.Expire(AfterDeadline);
        expired.ApplyPaymentSucceeded(PaymentId, AfterDeadline);

        confirmed.ApplyPaymentSucceeded(PaymentId, Now.AddMinutes(3)).ShouldBe(PaymentSucceededOutcome.Duplicate);
        expired.ApplyPaymentSucceeded(PaymentId, AfterDeadline).ShouldBe(PaymentSucceededOutcome.Duplicate);
    }

    [Fact]
    public void Second_payment_for_a_confirmed_reservation_is_refunded()
    {
        var reservation = AwaitingPayment();
        reservation.ApplyPaymentSucceeded(PaymentId, Now.AddMinutes(2));

        reservation.ApplyPaymentSucceeded(Guid.NewGuid(), Now.AddMinutes(3)).ShouldBe(PaymentSucceededOutcome.RefundRequired);
        reservation.Status.ShouldBe(ReservationStatus.Confirmed);
    }

    [Fact]
    public void Payment_failure_cancels_a_reservation_awaiting_payment()
    {
        var reservation = AwaitingPayment();

        reservation.ApplyPaymentFailed("card declined", Now.AddMinutes(2)).ShouldBeTrue();

        reservation.Status.ShouldBe(ReservationStatus.Cancelled);
        reservation.CancellationReason.ShouldBe("card declined");
    }

    [Fact]
    public void Payment_failure_after_the_reservation_ended_is_ignored()
    {
        var reservation = AwaitingPayment();
        reservation.Expire(AfterDeadline);

        reservation.ApplyPaymentFailed("card declined", AfterDeadline).ShouldBeFalse();
        reservation.Status.ShouldBe(ReservationStatus.Expired);
    }

    [Theory]
    [InlineData(ReservationStatus.Held)]
    [InlineData(ReservationStatus.AwaitingPayment)]
    public void Expiry_releases_held_and_unpaid_reservations_after_the_deadline(ReservationStatus status)
    {
        var reservation = status == ReservationStatus.Held ? Held() : AwaitingPayment();

        reservation.Expire(AfterDeadline.AddTicks(-1)).Error!.Code.ShouldBe("hold-active");
        reservation.Expire(AfterDeadline).IsSuccess.ShouldBeTrue();
        reservation.Status.ShouldBe(ReservationStatus.Expired);
    }

    [Fact]
    public void Confirmed_reservation_never_expires()
    {
        var reservation = AwaitingPayment();
        reservation.ApplyPaymentSucceeded(PaymentId, Now.AddMinutes(2));

        reservation.Expire(AfterDeadline).Error!.Code.ShouldBe("invalid-state");
    }

    [Fact]
    public void Every_step_bumps_the_version_and_is_recorded_in_the_timeline()
    {
        var reservation = AwaitingPayment();
        reservation.ApplyPaymentSucceeded(PaymentId, Now.AddMinutes(2));

        reservation.Version.ShouldBe(2);
        reservation.Timeline.Select(t => t.Step).ShouldBe(["Held", "PaymentRequested", "Confirmed"]);
    }
}
