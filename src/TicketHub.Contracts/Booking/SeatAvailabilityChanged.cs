using TicketHub.BuildingBlocks.Messaging;

namespace TicketHub.Contracts.Booking;

/// <summary>
/// Seats changed state in Booking (held, released or sold). Consumers must apply a seat's change only when its
/// <see cref="SeatStatusChange.Version"/> is newer than the one they hold, because delivery order is not guaranteed.
/// </summary>
[MessageType("booking.seat-availability-changed.v1")]
public sealed record SeatAvailabilityChanged(Guid EventId, IReadOnlyList<SeatStatusChange> Seats) : IIntegrationMessage;

/// <param name="Status">One of <c>Available</c>, <c>Held</c>, <c>Sold</c>.</param>
/// <param name="Version">Monotonic per seat; increases with every change.</param>
public sealed record SeatStatusChange(Guid SeatId, string Status, long Version);
