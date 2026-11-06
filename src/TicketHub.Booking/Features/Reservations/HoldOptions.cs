namespace TicketHub.Booking.Features.Reservations;

public sealed class HoldOptions
{
    public const string SectionName = "Booking:Holds";

    /// <summary>How long seats stay reserved for a customer before returning to sale.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(10);

    public int MaxSeats { get; set; } = 6;
}
