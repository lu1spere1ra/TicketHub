using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace TicketHub.Payments.Provider;

public sealed class PaymentSimulatorOptions
{
    public const string SectionName = "Payments:Simulator";

    /// <summary>Share of charges the "bank" declines (business failure, never retried).</summary>
    public double DeclineRate { get; set; } = 0.10;

    /// <summary>Share of calls that fail transiently (provider outage, retried with the same idempotency key).</summary>
    public double TransientFailureRate { get; set; } = 0.10;

    public TimeSpan MinLatency { get; set; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(800);

    /// <summary>Fixes the random sequence, for reproducible runs.</summary>
    public int? Seed { get; set; }
}

/// <summary>
/// Stands in for a real PSP, with configurable random latency, declines and outages. Deterministic test tokens
/// override the dice so tests and demos can choose an outcome:
/// <c>test-card-approve</c>, <c>test-card-decline</c>, and <c>test-card-flaky</c> (two outages, then approved).
/// <para>
/// Like a real PSP it honours idempotency keys: charging the same key twice returns the first result instead of
/// charging again. This is what protects the customer when Payments crashes after charging but before committing
/// and the message is redelivered.
/// </para>
/// </summary>
public sealed class SimulatedPaymentProvider : IPaymentProvider
{
    public const string ApproveToken = "test-card-approve";
    public const string DeclineToken = "test-card-decline";
    public const string FlakyToken = "test-card-flaky";
    private const int FlakyFailures = 2;

    private readonly PaymentSimulatorOptions _options;
    private readonly Random _random;
    private readonly Lock _randomLock = new();
    private readonly ConcurrentDictionary<string, ChargeResult> _charges = new();
    private readonly ConcurrentDictionary<string, int> _flakyAttempts = new();

    public SimulatedPaymentProvider(IOptions<PaymentSimulatorOptions> options)
    {
        _options = options.Value;
        _random = _options.Seed is { } seed ? new Random(seed) : new Random();
    }

    public async Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct)
    {
        await Task.Delay(NextLatency(), ct);

        if (_charges.TryGetValue(request.IdempotencyKey, out var previous))
        {
            return previous;
        }

        var result = Decide(request);
        return _charges.GetOrAdd(request.IdempotencyKey, result);
    }

    public async Task RefundAsync(string providerReference, CancellationToken ct) => await Task.Delay(NextLatency(), ct);

    private ChargeResult Decide(ChargeRequest request)
    {
        switch (request.PaymentToken)
        {
            case ApproveToken:
                return Approve();
            case DeclineToken:
                return new ChargeResult.Declined("Card declined by issuer");
            case FlakyToken:
                if (_flakyAttempts.AddOrUpdate(request.IdempotencyKey, 1, (_, n) => n + 1) <= FlakyFailures)
                {
                    throw new PaymentProviderUnavailableException("Simulated provider outage");
                }

                return Approve();
        }

        var roll = NextDouble();
        if (roll < _options.TransientFailureRate)
        {
            throw new PaymentProviderUnavailableException("Simulated provider outage");
        }

        return roll < _options.TransientFailureRate + _options.DeclineRate
            ? new ChargeResult.Declined("Card declined by issuer")
            : Approve();
    }

    private static ChargeResult.Approved Approve() => new("sim_" + Guid.NewGuid().ToString("N")[..16]);

    private TimeSpan NextLatency()
    {
        var min = _options.MinLatency.TotalMilliseconds;
        var max = Math.Max(min, _options.MaxLatency.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(min + (NextDouble() * (max - min)));
    }

    private double NextDouble()
    {
        lock (_randomLock)
        {
            return _random.NextDouble();
        }
    }
}
