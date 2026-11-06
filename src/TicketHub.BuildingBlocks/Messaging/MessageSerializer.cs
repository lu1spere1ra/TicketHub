using System.Text.Json;

namespace TicketHub.BuildingBlocks.Messaging;

internal static class MessageSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(object message) => JsonSerializer.Serialize(message, message.GetType(), Options);

    public static object? Deserialize(ReadOnlySpan<byte> body, Type type) => JsonSerializer.Deserialize(body, type, Options);
}
