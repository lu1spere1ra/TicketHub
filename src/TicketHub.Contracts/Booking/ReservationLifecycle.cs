using TicketHub.BuildingBlocks.Messaging;

namespace TicketHub.Contracts.Booking;

/// <summary>Payment succeeded and the seats are sold.</summary>
[MessageType("booking.reservation-confirmed.v1")]
public sealed record ReservationConfirmed(
    Guid ReservationId, Guid EventId, string CustomerEmail, IReadOnlyList<string> SeatLabels,
    decimal TotalAmount, string Currency, DateTimeOffset ConfirmedAt) : IIntegrationMessage;

/// <summary>The reservation ended without a purchase (payment declined); its seats are back on sale.</summary>
[MessageType("booking.reservation-cancelled.v1")]
public sealed record ReservationCancelled(
    Guid ReservationId, Guid EventId, string CustomerEmail, string Reason, DateTimeOffset CancelledAt) : IIntegrationMessage;

/// <summary>The hold ran out before payment completed; its seats are back on sale.</summary>
[MessageType("booking.reservation-expired.v1")]
public sealed record ReservationExpired(
    Guid ReservationId, Guid EventId, string CustomerEmail, DateTimeOffset ExpiredAt) : IIntegrationMessage;
