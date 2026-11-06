using System.Diagnostics.Metrics;

namespace TicketHub.Booking;

/// <summary>
/// Business counters, exported through OpenTelemetry (the ServiceDefaults listen to every <c>TicketHub.*</c> meter)
/// and visible in the Aspire dashboard. Hold conflicts are the headline number during a load test.
/// </summary>
internal static class BookingMetrics
{
    private static readonly Meter Meter = new("TicketHub.Booking");

    public static readonly Counter<long> HoldsSucceeded = Meter.CreateCounter<long>("tickethub.reservations.held");
    public static readonly Counter<long> HoldConflicts = Meter.CreateCounter<long>("tickethub.reservations.hold_conflicts",
        description: "Holds rejected because a seat was already taken.");
    public static readonly Counter<long> Confirmed = Meter.CreateCounter<long>("tickethub.reservations.confirmed");
    public static readonly Counter<long> Cancelled = Meter.CreateCounter<long>("tickethub.reservations.cancelled");
    public static readonly Counter<long> Expired = Meter.CreateCounter<long>("tickethub.reservations.expired");
    public static readonly Counter<long> RefundsRequested = Meter.CreateCounter<long>("tickethub.payments.refunds_requested");
}
