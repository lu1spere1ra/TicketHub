namespace TicketHub.Booking.Features.Admin;

/// <summary>A message that exhausted its retries in any service, archived for inspection.</summary>
public sealed class DeadLetter
{
    public Guid Id { get; init; }
    public string? MessageId { get; init; }
    public string? MessageType { get; init; }
    public string? OriginalQueue { get; init; }

    /// <summary>Kept as text, not jsonb: a poison message may not be valid JSON.</summary>
    public required string Payload { get; init; }

    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
    public DateTimeOffset ArchivedAt { get; init; }
}
