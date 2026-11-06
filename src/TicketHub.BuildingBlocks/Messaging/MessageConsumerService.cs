using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace TicketHub.BuildingBlocks.Messaging;

/// <summary>
/// Consumes the service's queues. For each delivery:
/// <list type="number">
/// <item>Insert (message id, consumer) into the inbox, inside a database transaction. A conflict means the message
/// was already processed: commit, ack and skip.</item>
/// <item>Run the handler and <c>SaveChanges</c> on the same scoped DbContext, then commit, then ack.</item>
/// <item>On failure, republish to the queue's retry queue with an incremented attempt; after
/// <see cref="MessagingOptions.MaxAttempts"/>, publish to the dead-letter queue with the error details.</item>
/// </list>
/// </summary>
internal sealed partial class MessageConsumerService<TDbContext>(
    IConnection connection,
    IServiceScopeFactory scopeFactory,
    ConsumerRegistry registry,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<MessageConsumerService<TDbContext>> logger) : IHostedService, IDisposable
    where TDbContext : DbContext
{
    private readonly MessagingOptions _options = options.Value;
    private readonly List<IChannel> _channels = [];
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Declares queues and starts consuming before the host reports started, so nothing published after startup
    /// can arrive at an exchange whose queues do not exist yet.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var queue in registry.Bindings.GroupBy(b => b.Queue))
        {
            // Confirms are enabled because this channel also republishes failed messages to the retry and
            // dead-letter queues; the original is acked only after that republish is confirmed.
            var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken);
            _channels.Add(channel);

            var queueName = _options.QueuePrefix + queue.Key;
            await RabbitMqTopology.DeclareConsumerQueueAsync(
                channel, _options, queueName, queue.Select(b => b.MessageTypeName), cancellationToken);
            await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: _options.PrefetchCount, global: false, cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            var logicalQueue = queue.Key;
            consumer.ReceivedAsync += (_, delivery) => OnReceivedAsync(channel, logicalQueue, queueName, delivery, _stopping.Token);
            await channel.BasicConsumeAsync(queueName, autoAck: false, consumer, cancellationToken);
            LogConsuming(queueName);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Idempotent: hosts may call StopAsync more than once during shutdown.
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        await _stopping.CancelAsync();
        foreach (var channel in _channels)
        {
            try
            {
                if (channel.IsOpen)
                {
                    await channel.CloseAsync(cancellationToken);
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
                // The connection is already going away; nothing left to close.
            }

            channel.Dispose();
        }

        _channels.Clear();
    }

    public void Dispose() => _stopping.Dispose();

    /// <param name="queue">Logical queue name: identifies the handler and the inbox consumer.</param>
    /// <param name="physicalQueue">Broker queue name (logical name plus <see cref="MessagingOptions.QueuePrefix"/>).</param>
    private async Task OnReceivedAsync(IChannel channel, string queue, string physicalQueue, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var properties = delivery.BasicProperties;
        var attempt = MessageHeaders.ReadInt(properties.Headers, MessageHeaders.Attempt) ?? 1;
        var typeName = properties.Type ?? "unknown";

        ActivityContext.TryParse(
            MessageHeaders.ReadString(properties.Headers, MessageHeaders.TraceParent),
            MessageHeaders.ReadString(properties.Headers, MessageHeaders.TraceState),
            out var parent);
        using var activity = MessagingTelemetry.ActivitySource.StartActivity($"{typeName} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", physicalQueue);
        activity?.SetTag("messaging.message.id", properties.MessageId);
        activity?.SetTag("messaging.attempt", attempt);

        try
        {
            try
            {
                var (binding, message, context) = Decode(queue, delivery, attempt);
                var processed = await ProcessAsync(binding, message, context, ct);
                (processed ? MessagingTelemetry.Consumed : MessagingTelemetry.Duplicates)
                    .Add(1, new KeyValuePair<string, object?>("messaging.message.type", typeName));
            }
            catch (PoisonMessageException ex)
            {
                // Retrying cannot fix a message we cannot even read.
                await DeadLetterAsync(channel, physicalQueue, delivery, attempt, ex, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                if (attempt < _options.MaxAttempts)
                {
                    LogRetrying(ex, typeName, attempt, _options.MaxAttempts);
                    await RetryAsync(channel, physicalQueue, delivery, attempt, ct);
                }
                else
                {
                    await DeadLetterAsync(channel, physicalQueue, delivery, attempt, ex, ct);
                }
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex)
        {
            // Shutting down mid-message, or the retry/dead-letter republish itself failed: give the message back to
            // the broker untouched. The inbox makes the redelivery safe.
            LogRequeued(ex, typeName);
            if (channel.IsOpen)
            {
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
            }
        }
    }

    private (ConsumerBinding Binding, object Message, MessageContext Context) Decode(string queue, BasicDeliverEventArgs delivery, int attempt)
    {
        var properties = delivery.BasicProperties;
        if (!Guid.TryParse(properties.MessageId, out var messageId))
        {
            throw new PoisonMessageException("Message has no valid message id.");
        }

        var binding = registry.Find(queue, properties.Type)
            ?? throw new PoisonMessageException($"Queue '{queue}' has no handler for message type '{properties.Type}'.");

        object? message;
        try
        {
            message = MessageSerializer.Deserialize(delivery.Body.Span, binding.MessageType);
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"Payload is not a valid {binding.MessageTypeName}.", ex);
        }

        if (message is null)
        {
            throw new PoisonMessageException("Payload is empty.");
        }

        var context = new MessageContext(
            messageId,
            binding.MessageTypeName,
            Guid.TryParse(properties.CorrelationId, out var correlationId) ? correlationId : null,
            attempt,
            DateTimeOffset.FromUnixTimeSeconds(properties.Timestamp.UnixTime));

        return (binding, message, context);
    }

    private async Task<bool> ProcessAsync(ConsumerBinding binding, object message, MessageContext context, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);

            // Inserting first (rather than checking first) means a concurrent duplicate blocks on the primary key
            // until this transaction finishes, then inserts nothing. Duplicates are serialised, not raced.
            var inserted = await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "InboxMessages" ("MessageId", "Consumer", "ProcessedAt")
                VALUES ({context.MessageId}, {binding.Queue}, {timeProvider.GetUtcNow()})
                ON CONFLICT DO NOTHING
                """, token);

            if (inserted == 0)
            {
                await transaction.CommitAsync(token);
                return false;
            }

            var handler = scope.ServiceProvider.GetRequiredService(binding.HandlerType);
            await binding.Invoke(handler, message, context, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return true;
        }, ct);
    }

    private async Task RetryAsync(IChannel channel, string queue, BasicDeliverEventArgs delivery, int attempt, CancellationToken ct)
    {
        var properties = CopyProperties(delivery, headers => headers[MessageHeaders.Attempt] = attempt + 1);
        await channel.BasicPublishAsync(string.Empty, RabbitMqTopology.RetryQueueFor(queue), mandatory: false, properties, delivery.Body, ct);
        MessagingTelemetry.Retried.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", queue));
    }

    private async Task DeadLetterAsync(IChannel channel, string queue, BasicDeliverEventArgs delivery, int attempt, Exception error, CancellationToken ct)
    {
        LogDeadLettered(error, delivery.BasicProperties.Type, queue, attempt);
        var properties = CopyProperties(delivery, headers =>
        {
            headers[MessageHeaders.Attempt] = attempt;
            headers[MessageHeaders.OriginalQueue] = queue;
            headers[MessageHeaders.ExceptionType] = error.GetType().FullName;
            headers[MessageHeaders.ExceptionMessage] = Truncate(error.Message, 1000);
            headers[MessageHeaders.FailedAt] = timeProvider.GetUtcNow().ToString("O");
        });
        await channel.BasicPublishAsync(string.Empty, _options.DeadLetterQueue, mandatory: false, properties, delivery.Body, ct);
        MessagingTelemetry.DeadLettered.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", queue));
    }

    private static BasicProperties CopyProperties(BasicDeliverEventArgs delivery, Action<Dictionary<string, object?>> mutateHeaders)
    {
        var properties = new BasicProperties(delivery.BasicProperties);
        var headers = delivery.BasicProperties.Headers is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(delivery.BasicProperties.Headers);
        mutateHeaders(headers);
        properties.Headers = headers;
        return properties;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming queue {Queue}")]
    private partial void LogConsuming(string queue);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handling {MessageType} failed on attempt {Attempt}/{MaxAttempts}; scheduling a retry")]
    private partial void LogRetrying(Exception exception, string messageType, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dead-lettering {MessageType} from {Queue} after {Attempt} attempt(s)")]
    private partial void LogDeadLettered(Exception exception, string? messageType, string queue, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Returning {MessageType} to the broker for redelivery")]
    private partial void LogRequeued(Exception exception, string messageType);
}

internal sealed class PoisonMessageException(string message, Exception? inner = null) : Exception(message, inner);
