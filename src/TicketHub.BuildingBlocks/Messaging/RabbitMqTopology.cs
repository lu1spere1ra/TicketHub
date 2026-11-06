using System.Text;
using RabbitMQ.Client;

namespace TicketHub.BuildingBlocks.Messaging;

/// <summary>
/// Declares the broker objects a service relies on. Declarations are idempotent, so every service declares what it
/// uses at startup and there is no separate provisioning step.
/// </summary>
internal static class RabbitMqTopology
{
    public static string RetryQueueFor(string queue) => queue + ".retry";

    public static Task DeclareExchangeAsync(IChannel channel, MessagingOptions options, CancellationToken ct) =>
        channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);

    public static async Task DeclareConsumerQueueAsync(
        IChannel channel, MessagingOptions options, string queue, IEnumerable<string> routingKeys, CancellationToken ct)
    {
        await DeclareExchangeAsync(channel, options, ct);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);

        // A failed message is parked here; when its TTL expires the broker dead-letters it straight back to the
        // consumer's own queue (default exchange, routing key = queue name), so a retry never re-fans out to
        // other services' queues.
        await channel.QueueDeclareAsync(
            RetryQueueFor(queue), durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = (int)options.RetryDelay.TotalMilliseconds,
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = queue,
            },
            cancellationToken: ct);

        await DeclareDeadLetterQueueAsync(channel, options, ct);

        foreach (var routingKey in routingKeys)
        {
            await channel.QueueBindAsync(queue, options.Exchange, routingKey, cancellationToken: ct);
        }
    }

    public static Task DeclareDeadLetterQueueAsync(IChannel channel, MessagingOptions options, CancellationToken ct) =>
        channel.QueueDeclareAsync(options.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);

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
