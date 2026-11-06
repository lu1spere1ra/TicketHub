using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TicketHub.BuildingBlocks.Messaging;

internal static class MessagingTelemetry
{
    public const string Name = "TicketHub.Messaging";

    public static readonly ActivitySource ActivitySource = new(Name);
    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Published = Meter.CreateCounter<long>("tickethub.messages.published");
    public static readonly Counter<long> Consumed = Meter.CreateCounter<long>("tickethub.messages.consumed");
    public static readonly Counter<long> Duplicates = Meter.CreateCounter<long>("tickethub.messages.duplicates");
    public static readonly Counter<long> Retried = Meter.CreateCounter<long>("tickethub.messages.retried");
    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>("tickethub.messages.dead_lettered");
}
