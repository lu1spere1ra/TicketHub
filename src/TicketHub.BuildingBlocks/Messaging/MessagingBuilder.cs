using Microsoft.Extensions.DependencyInjection;

namespace TicketHub.BuildingBlocks.Messaging;

internal sealed record ConsumerBinding(
    string Queue,
    string MessageTypeName,
    Type MessageType,
    Type HandlerType,
    Func<object, object, MessageContext, CancellationToken, Task> Invoke);

internal sealed class ConsumerRegistry
{
    private readonly List<ConsumerBinding> _bindings = [];

    public IReadOnlyList<ConsumerBinding> Bindings => _bindings;

    public void Add(ConsumerBinding binding)
    {
        if (_bindings.Any(b => b.Queue == binding.Queue && b.MessageTypeName == binding.MessageTypeName))
        {
            throw new InvalidOperationException($"Queue '{binding.Queue}' already has a handler for '{binding.MessageTypeName}'.");
        }

        _bindings.Add(binding);
    }

    public ConsumerBinding? Find(string queue, string? messageTypeName) =>
        _bindings.FirstOrDefault(b => b.Queue == queue && b.MessageTypeName == messageTypeName);
}

/// <summary>Declares which messages a service consumes and which queue each one arrives on.</summary>
public sealed class MessagingBuilder
{
    private readonly IServiceCollection _services;

    internal MessagingBuilder(IServiceCollection services) => _services = services;

    internal ConsumerRegistry Registry { get; } = new();

    public MessagingBuilder Consumes<TMessage, THandler>(string queue)
        where TMessage : class, IIntegrationMessage
        where THandler : class, IMessageHandler<TMessage>
    {
        _services.AddScoped<THandler>();
        Registry.Add(new ConsumerBinding(
            queue,
            MessageTypeAttribute.NameOf(typeof(TMessage)),
            typeof(TMessage),
            typeof(THandler),
            static (handler, message, context, ct) =>
                ((IMessageHandler<TMessage>)handler).HandleAsync((TMessage)message, context, ct)));
        return this;
    }
}
