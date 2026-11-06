using TicketHub.BuildingBlocks;

namespace TicketHub.Payments.Provider;

public sealed record ChargeRequest(string IdempotencyKey, Money Amount, string PaymentToken);

public abstract record ChargeResult
{
    public sealed record Approved(string ProviderReference) : ChargeResult;

    public sealed record Declined(string Reason) : ChargeResult;
}

/// <summary>The provider could not answer (timeout, 5xx). Safe to retry with the same idempotency key.</summary>
public sealed class PaymentProviderUnavailableException(string message) : Exception(message);

/// <summary>An external payment service provider (PSP).</summary>
public interface IPaymentProvider
{
    /// <exception cref="PaymentProviderUnavailableException">Transient failure; retry later.</exception>
    Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct);

    Task RefundAsync(string providerReference, CancellationToken ct);
}
