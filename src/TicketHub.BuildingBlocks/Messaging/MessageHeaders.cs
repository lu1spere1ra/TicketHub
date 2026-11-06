using System.Text;

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

    /// <summary>Reads a header as text; RabbitMQ delivers string headers as UTF-8 byte arrays.</summary>
    public static string? ReadString(IDictionary<string, object?>? headers, string key) =>
        headers is not null && headers.TryGetValue(key, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                null => null,
                _ => value.ToString(),
            }
            : null;

    public static int? ReadInt(IDictionary<string, object?>? headers, string key) =>
        headers is not null && headers.TryGetValue(key, out var value)
            ? value switch
            {
                int i => i,
                long l => (int)l,
                byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
                _ => null,
            }
            : null;
}
