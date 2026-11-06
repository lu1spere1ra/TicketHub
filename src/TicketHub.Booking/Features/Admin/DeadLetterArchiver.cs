using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TicketHub.Booking.Data;
using TicketHub.BuildingBlocks.Messaging;

namespace TicketHub.Booking.Features.Admin;

/// <summary>
/// Drains the shared dead-letter queue into a table so the admin endpoint can list and filter failures from every
/// service. Hosting this in Booking avoids an extra deployable; in a larger system it would live in an ops service.
/// </summary>
public sealed partial class DeadLetterArchiver(
    IConnection connection,
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<DeadLetterArchiver> logger) : IHostedService, IDisposable
{
    private IChannel? _channel;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await _channel.QueueDeclareAsync(options.Value.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await _channel.BasicQosAsync(0, 16, false, cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;
        await _channel.BasicConsumeAsync(options.Value.DeadLetterQueue, autoAck: false, consumer, cancellationToken);
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        try
        {
            var headers = delivery.BasicProperties.Headers;
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
            db.DeadLetters.Add(new DeadLetter
            {
                Id = Guid.CreateVersion7(),
                MessageId = delivery.BasicProperties.MessageId,
                MessageType = delivery.BasicProperties.Type,
                OriginalQueue = MessageHeaders.ReadString(headers, MessageHeaders.OriginalQueue),
                Payload = Encoding.UTF8.GetString(delivery.Body.Span),
                ExceptionType = MessageHeaders.ReadString(headers, MessageHeaders.ExceptionType),
                ExceptionMessage = MessageHeaders.ReadString(headers, MessageHeaders.ExceptionMessage),
                Attempts = MessageHeaders.ReadInt(headers, MessageHeaders.Attempt) ?? 1,
                FailedAt = DateTimeOffset.TryParse(MessageHeaders.ReadString(headers, MessageHeaders.FailedAt), out var failedAt) ? failedAt : null,
                ArchivedAt = timeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync();
            await _channel!.BasicAckAsync(delivery.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            LogArchiveFailed(ex);
            await _channel!.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            try
            {
                await _channel.CloseAsync(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                // Connection already gone.
            }
        }
    }

    public void Dispose() => _channel?.Dispose();

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not archive a dead-lettered message; it stays on the queue")]
    private partial void LogArchiveFailed(Exception exception);
}
