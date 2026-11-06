namespace TicketHub.BuildingBlocks.Messaging;

public static class MessageHeaders
{
    public const string TraceParent = "traceparent";
    public const string TraceState = "tracestate";
    public const string Attempt = "x-attempt";
    public const string OriginalQueue = "x-original-queue";
    public const string ExceptionType = "x-exception-type";
    public const string ExceptionMessage = "x-exception-message";
    public const string FailedAt = "x-failed-at";
}
