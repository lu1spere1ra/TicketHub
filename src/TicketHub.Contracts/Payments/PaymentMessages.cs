using TicketHub.BuildingBlocks.Messaging;

namespace TicketHub.Contracts.Payments;

// Commands are owned by the service that handles them (Payments); events by the one that publishes them.

/// <summary>Charge the customer for a reservation. The reservation id is the idempotency key.</summary>
[MessageType("payments.request-payment.v1")]
public sealed record RequestPayment(Guid ReservationId, decimal Amount, string Currency, string PaymentToken) : IIntegrationMessage;

/// <summary>Compensation: give the money back for a payment whose reservation could not be honoured.</summary>
[MessageType("payments.refund-payment.v1")]
public sealed record RefundPayment(Guid PaymentId, Guid ReservationId, string Reason) : IIntegrationMessage;

[MessageType("payments.payment-succeeded.v1")]
public sealed record PaymentSucceeded(Guid PaymentId, Guid ReservationId, decimal Amount, string Currency, DateTimeOffset SucceededAt) : IIntegrationMessage;

[MessageType("payments.payment-failed.v1")]
public sealed record PaymentFailed(Guid PaymentId, Guid ReservationId, string Reason, DateTimeOffset FailedAt) : IIntegrationMessage;

[MessageType("payments.payment-refunded.v1")]
public sealed record PaymentRefunded(Guid PaymentId, Guid ReservationId, DateTimeOffset RefundedAt) : IIntegrationMessage;
