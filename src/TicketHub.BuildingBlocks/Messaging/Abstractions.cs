using System.Reflection;

namespace TicketHub.BuildingBlocks.Messaging;

/// <summary>Marker for messages that cross service boundaries (commands and events).</summary>
public interface IIntegrationMessage;

/// <summary>
/// Stable wire name of a message, also used as its routing key, e.g. <c>booking.seat-availability-changed.v1</c>.
/// Decouples the contract from CLR type names, so types can be renamed or moved without breaking consumers.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MessageTypeAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    public static string NameOf(Type type) =>
        type.GetCustomAttribute<MessageTypeAttribute>()?.Name
        ?? throw new InvalidOperationException($"{type.Name} has no [MessageType] attribute.");

    public static string NameOf<T>() where T : IIntegrationMessage => NameOf(typeof(T));
}

/// <summary>Delivery metadata passed to handlers alongside the message.</summary>
public sealed record MessageContext(Guid MessageId, string MessageType, Guid? CorrelationId, int Attempt, DateTimeOffset OccurredAt);

/// <summary>
/// Handles one message type. Runs inside the consumer's database transaction, together with the inbox insert,
/// so database changes made through the scoped DbContext commit atomically with the deduplication record.
/// </summary>
public interface IMessageHandler<in TMessage> where TMessage : class, IIntegrationMessage
{
    Task HandleAsync(TMessage message, MessageContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Adds a message to the current unit of work. It is published only if, and after, <c>SaveChanges</c> commits.
/// </summary>
public interface IOutbox
{
    void Enqueue<TMessage>(TMessage message, Guid? correlationId = null) where TMessage : class, IIntegrationMessage;
}
