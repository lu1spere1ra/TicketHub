using TicketHub.BuildingBlocks;

namespace TicketHub.BuildingBlocks.Tests.Domain;

public class MoneyTests
{
    [Fact]
    public void Adding_amounts_in_the_same_currency_sums_them()
    {
        (new Money(10.50m, "EUR") + new Money(2.25m, "EUR")).ShouldBe(new Money(12.75m, "EUR"));
    }

    [Fact]
    public void Adding_amounts_in_different_currencies_throws()
    {
        Should.Throw<InvalidOperationException>(() => new Money(1m, "EUR") + new Money(1m, "USD"));
    }

    [Theory]
    [InlineData(12.34, 1234)]
    [InlineData(0.01, 1)]
    [InlineData(100, 10000)]
    public void Minor_units_round_trip(decimal amount, long minor)
    {
        new Money(amount, "EUR").ToMinorUnits().ShouldBe(minor);
        Money.FromMinorUnits(minor, "EUR").Amount.ShouldBe(amount);
    }
}
