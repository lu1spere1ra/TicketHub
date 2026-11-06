using System.Collections.Concurrent;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace TicketHub.TestKit;

/// <summary>Talks to the broker directly, bypassing the building block, to publish or observe raw messages.</summary>
public sealed class RawBroker : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;

    private RawBroker(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    public static async Task<RawBroker> ConnectAsync(string connectionString)
    {
        var connection = await new ConnectionFactory { Uri = new Uri(connectionString) }.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        return new RawBroker(connection, channel);
    }

    public async Task PublishAsync(string exchange, string type, Guid messageId, string body)
    {
        var properties = new BasicProperties { MessageId = messageId.ToString(), Type = type, ContentType = "application/json" };
        await _channel.BasicPublishAsync(exchange, type, mandatory: false, properties, Encoding.UTF8.GetBytes(body));
    }

    /// <summary>Publishes a message exactly as the outbox relay would (camelCase JSON, type as routing key).</summary>
    public Task PublishMessageAsync<T>(string exchange, string type, T message, Guid? messageId = null) =>
        PublishAsync(exchange, type, messageId ?? Guid.CreateVersion7(),
            System.Text.Json.JsonSerializer.Serialize(message, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

    /// <summary>Binds a fresh queue to the exchange and records every message id delivered to it.</summary>
    public async Task<ConcurrentBag<string>> ObserveAsync(string exchange, string routingKey)
    {
        var seen = new ConcurrentBag<string>();
        await _channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true);
        var queue = (await _channel.QueueDeclareAsync(string.Empty, durable: false, exclusive: true, autoDelete: true)).QueueName;
        await _channel.QueueBindAsync(queue, exchange, routingKey);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            seen.Add(delivery.BasicProperties.MessageId!);
            return Task.CompletedTask;
        };
        await _channel.BasicConsumeAsync(queue, autoAck: true, consumer);
        return seen;
    }

    public async Task<BasicGetResult?> GetAsync(string queue) => await _channel.BasicGetAsync(queue, autoAck: true);

    public async ValueTask DisposeAsync()
    {
        await _channel.CloseAsync();
        await _connection.CloseAsync();
        _channel.Dispose();
        _connection.Dispose();
    }
}
