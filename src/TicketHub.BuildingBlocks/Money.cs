namespace TicketHub.BuildingBlocks;

/// <summary>An amount in a currency. Arithmetic across currencies is a programming error.</summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    public const string Euro = "EUR";

    public static Money Zero(string currency) => new(0m, currency);

    public static Money operator +(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException($"Cannot add {left.Currency} to {right.Currency}.");
        }

        return left with { Amount = left.Amount + right.Amount };
    }

    /// <summary>Integer minor units (cents), as used on the wire to avoid decimal encoding issues.</summary>
    public long ToMinorUnits() => (long)decimal.Round(Amount * 100m, 0, MidpointRounding.AwayFromZero);

    public static Money FromMinorUnits(long minorUnits, string currency) => new(minorUnits / 100m, currency);

    public override string ToString() => $"{Amount:0.00} {Currency}";
}
