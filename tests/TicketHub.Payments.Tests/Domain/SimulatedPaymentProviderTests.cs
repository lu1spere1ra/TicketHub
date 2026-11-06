using Microsoft.Extensions.Options;
using TicketHub.BuildingBlocks;
using TicketHub.Payments.Provider;

namespace TicketHub.Payments.Tests.Domain;

public class SimulatedPaymentProviderTests
{
    private static readonly Money TwentyFive = new(25m, Money.Euro);

    private static SimulatedPaymentProvider Provider(double declineRate = 0, double transientRate = 0, int? seed = 42) =>
        new(Options.Create(new PaymentSimulatorOptions
        {
            DeclineRate = declineRate,
            TransientFailureRate = transientRate,
            MinLatency = TimeSpan.Zero,
            MaxLatency = TimeSpan.Zero,
            Seed = seed,
        }));

    private static Task<ChargeResult> Charge(SimulatedPaymentProvider provider, string token, string? key = null) =>
        provider.ChargeAsync(new ChargeRequest(key ?? Guid.NewGuid().ToString(), TwentyFive, token), CancellationToken.None);

    [Fact]
    public async Task Approve_token_always_approves()
    {
        var provider = Provider(declineRate: 1);

        (await Charge(provider, SimulatedPaymentProvider.ApproveToken)).ShouldBeOfType<ChargeResult.Approved>();
    }

    [Fact]
    public async Task Decline_token_always_declines()
    {
        (await Charge(Provider(), SimulatedPaymentProvider.DeclineToken)).ShouldBeOfType<ChargeResult.Declined>();
    }

    [Fact]
    public async Task Flaky_token_fails_twice_then_approves_for_the_same_key()
    {
        var provider = Provider();

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() => Charge(provider, SimulatedPaymentProvider.FlakyToken, "k"));
        await Should.ThrowAsync<PaymentProviderUnavailableException>(() => Charge(provider, SimulatedPaymentProvider.FlakyToken, "k"));
        (await Charge(provider, SimulatedPaymentProvider.FlakyToken, "k")).ShouldBeOfType<ChargeResult.Approved>();
    }

    [Fact]
    public async Task Same_idempotency_key_returns_the_first_result_without_charging_again()
    {
        var provider = Provider();

        var first = await Charge(provider, SimulatedPaymentProvider.ApproveToken, "reservation-1");
        var second = await Charge(provider, SimulatedPaymentProvider.DeclineToken, "reservation-1");

        second.ShouldBe(first);
    }

    [Fact]
    public async Task Configured_rates_are_respected_within_tolerance()
    {
        var provider = Provider(declineRate: 0.2, transientRate: 0.1, seed: 7);
        int declined = 0, transient = 0;
        const int draws = 10_000;

        for (var i = 0; i < draws; i++)
        {
            try
            {
                if (await Charge(provider, "random-card") is ChargeResult.Declined)
                {
                    declined++;
                }
            }
            catch (PaymentProviderUnavailableException)
            {
                transient++;
            }
        }

        ((double)declined / draws).ShouldBe(0.2, tolerance: 0.02);
        ((double)transient / draws).ShouldBe(0.1, tolerance: 0.02);
    }
}
