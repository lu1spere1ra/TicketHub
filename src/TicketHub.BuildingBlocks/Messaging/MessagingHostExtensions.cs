using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace TicketHub.BuildingBlocks.Messaging;

public static class MessagingHostExtensions
{
    /// <summary>
    /// Wires RabbitMQ, the outbox (publishing) and the inbox-backed consumers for one service. The service's
    /// DbContext must call <see cref="MessagingModelBuilderExtensions.AddMessagingTables"/>.
    /// </summary>
    public static IHostApplicationBuilder AddTicketHubMessaging<TDbContext>(
        this IHostApplicationBuilder builder,
        Action<MessagingBuilder>? configure = null,
        string connectionName = "rabbitmq")
        where TDbContext : DbContext
    {
        // The messaging layer emits its own producer/consumer spans, linked across the outbox; the client
        // library's spans would duplicate them.
        builder.AddRabbitMQClient(connectionName, settings => settings.DisableTracing = true);

        builder.Services.AddOptions<MessagingOptions>().Bind(builder.Configuration.GetSection(MessagingOptions.SectionName));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IOutbox, EfOutbox<TDbContext>>();
        builder.Services.AddHostedService<OutboxRelay<TDbContext>>();

        var messaging = new MessagingBuilder(builder.Services);
        configure?.Invoke(messaging);
        builder.Services.AddSingleton(messaging.Registry);
        if (messaging.Registry.Bindings.Count > 0)
        {
            builder.Services.AddHostedService<MessageConsumerService<TDbContext>>();
        }

        return builder;
    }
}
