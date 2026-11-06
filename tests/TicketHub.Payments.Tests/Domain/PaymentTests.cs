using TicketHub.BuildingBlocks;
using TicketHub.Payments.Domain;

namespace TicketHub.Payments.Tests.Domain;

public class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly Money Amount = new(50m, Money.Euro);

    [Fact]
    public void Succeeded_payment_can_be_refunded_once()
    {
        var payment = Payment.Succeeded(Guid.NewGuid(), Amount, "sim_1", Now);

        payment.Refund(Now).Value.ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.Refunded);
        payment.RefundedAt.ShouldBe(Now);
    }

    [Fact]
    public void Refund_is_idempotent()
    {
        var payment = Payment.Succeeded(Guid.NewGuid(), Amount, "sim_1", Now);
        payment.Refund(Now);

        payment.Refund(Now.AddMinutes(1)).Value.ShouldBeFalse();
        payment.RefundedAt.ShouldBe(Now);
    }

    [Fact]
    public void Declined_payment_cannot_be_refunded()
    {
        Payment.Declined(Guid.NewGuid(), Amount, "no funds", Now).Refund(Now).IsSuccess.ShouldBeFalse();
    }
}
