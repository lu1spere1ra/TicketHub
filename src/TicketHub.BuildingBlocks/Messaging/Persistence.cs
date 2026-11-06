using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace TicketHub.BuildingBlocks.Messaging;

/// <summary>A message written in the same transaction as the business change, published later by the relay.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public required string Type { get; init; }
    public required string Payload { get; init; }
    public Guid? CorrelationId { get; init; }

    /// <summary>W3C trace context of the request that produced the message, restored when it is published.</summary>
    public string? TraceParent { get; init; }
    public string? TraceState { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>Records that a consumer has processed a message id; the primary key makes reprocessing impossible.</summary>
public sealed class InboxMessage
{
    public Guid MessageId { get; init; }
    public required string Consumer { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
}

public static class MessagingModelBuilderExtensions
{
    public const string OutboxTable = "OutboxMessages";
    public const string InboxTable = "InboxMessages";

    public static ModelBuilder AddMessagingTables(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable(OutboxTable);
            outbox.HasKey(m => m.Id);
            outbox.Property(m => m.Type).HasMaxLength(200);
            outbox.Property(m => m.Payload).HasColumnType("jsonb");
            outbox.Property(m => m.TraceParent).HasMaxLength(64);
            outbox.Property(m => m.TraceState).HasMaxLength(512);
            // The relay only ever looks for unsent rows; a partial index keeps that scan tiny.
            outbox.HasIndex(m => m.Id).HasFilter("\"SentAt\" IS NULL").HasDatabaseName("IX_OutboxMessages_Unsent");
        });

        modelBuilder.Entity<InboxMessage>(inbox =>
        {
            inbox.ToTable(InboxTable);
            inbox.HasKey(m => new { m.MessageId, m.Consumer });
            inbox.Property(m => m.Consumer).HasMaxLength(200);
        });

        return modelBuilder;
    }
}

internal sealed class EfOutbox<TDbContext>(TDbContext dbContext, TimeProvider timeProvider) : IOutbox
    where TDbContext : DbContext
{
    public void Enqueue<TMessage>(TMessage message, Guid? correlationId = null) where TMessage : class, IIntegrationMessage
    {
        ArgumentNullException.ThrowIfNull(message);

        var activity = Activity.Current;
        dbContext.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = MessageTypeAttribute.NameOf(typeof(TMessage)),
            Payload = MessageSerializer.Serialize(message),
            CorrelationId = correlationId,
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString,
            CreatedAt = timeProvider.GetUtcNow(),
        });
    }
}
