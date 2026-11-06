using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace TicketHub.BuildingBlocks.Messaging;

/// <summary>
/// Publishes committed outbox rows to RabbitMQ.
/// <list type="bullet">
/// <item>Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>, so several replicas can run the relay without
/// publishing the same row concurrently.</item>
/// <item>A row is marked sent only after the broker confirms it (publisher confirms). A crash between confirm and
/// commit republishes the row: delivery is at-least-once, and consumers deduplicate.</item>
/// </list>
/// </summary>
internal sealed partial class OutboxRelay<TDbContext>(
    IConnection connection,
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRelay<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);
    private readonly MessagingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IChannel? channel = null;
        var nextPurge = timeProvider.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            try
            {
                if (channel is null || channel.IsClosed)
                {
                    channel?.Dispose();
                    channel = await connection.CreateChannelAsync(
                        new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                        stoppingToken);
                    await RabbitMqTopology.DeclareExchangeAsync(channel, _options, stoppingToken);
                }

                published = await PublishBatchAsync(channel, stoppingToken);

                if (timeProvider.GetUtcNow() >= nextPurge)
                {
                    await PurgeSentMessagesAsync(stoppingToken);
                    nextPurge = timeProvider.GetUtcNow() + PurgeInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogRelayFailed(ex);
            }

            if (published < _options.OutboxBatchSize)
            {
                try
                {
                    // Real time on purpose: polling cadence is infrastructure, while TimeProvider is business time (tests freeze it).
                    await Task.Delay(_options.OutboxPollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        channel?.Dispose();
    }

    private async Task<int> PublishBatchAsync(IChannel channel, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);

            var batch = await db.Set<OutboxMessage>()
                .FromSql($"""
                    SELECT * FROM "OutboxMessages"
                    WHERE "SentAt" IS NULL
                    ORDER BY "Id"
                    LIMIT {_options.OutboxBatchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(token);

            foreach (var message in batch)
            {
                await PublishAsync(channel, message, token);
                message.SentAt = timeProvider.GetUtcNow();
            }

            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return batch.Count;
        }, ct);
    }

    private async Task PublishAsync(IChannel channel, OutboxMessage message, CancellationToken ct)
    {
        // The relay runs on its own loop, long after the originating request finished. Restoring the stored context
        // as the parent keeps the publish span (and everything downstream) in the request's trace.
        ActivityContext.TryParse(message.TraceParent, message.TraceState, out var parent);
        using var activity = MessagingTelemetry.ActivitySource.StartActivity($"{message.Type} publish", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", _options.Exchange);
        activity?.SetTag("messaging.message.id", message.Id);

        var headers = new Dictionary<string, object?>();
        var traceParent = activity?.Id ?? message.TraceParent;
        var traceState = activity?.TraceStateString ?? message.TraceState;
        if (traceParent is not null)
        {
            headers[MessageHeaders.TraceParent] = traceParent;
        }

        if (traceState is not null)
        {
            headers[MessageHeaders.TraceState] = traceState;
        }

        var properties = new BasicProperties
        {
            MessageId = message.Id.ToString(),
            Type = message.Type,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            CorrelationId = message.CorrelationId?.ToString(),
            Timestamp = new AmqpTimestamp(message.CreatedAt.ToUnixTimeSeconds()),
            Headers = headers,
        };

        // With confirmation tracking enabled this completes only once the broker has confirmed the message.
        await channel.BasicPublishAsync(_options.Exchange, message.Type, mandatory: false, properties, Encoding.UTF8.GetBytes(message.Payload), ct);
        MessagingTelemetry.Published.Add(1, new KeyValuePair<string, object?>("messaging.message.type", message.Type));
    }

    private async Task PurgeSentMessagesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var cutoff = timeProvider.GetUtcNow() - _options.OutboxRetention;
        await db.Set<OutboxMessage>().Where(m => m.SentAt != null && m.SentAt < cutoff).ExecuteDeleteAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox relay iteration failed; retrying after the poll interval")]
    private partial void LogRelayFailed(Exception exception);
}
