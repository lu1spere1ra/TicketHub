using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TicketHub.BuildingBlocks.Messaging;

namespace TicketHub.BuildingBlocks.Tests.Messaging;

[MessageType("test.widget-created.v1")]
public sealed record WidgetCreated(Guid WidgetId, string Name) : IIntegrationMessage;

public sealed class Widget
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
}

public sealed class ProcessedWidget
{
    public Guid Id { get; init; }
    public Guid WidgetId { get; init; }
    public int Attempt { get; init; }
}

public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();
    public DbSet<ProcessedWidget> ProcessedWidgets => Set<ProcessedWidget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddMessagingTables();
}

/// <summary>Lets a test observe and steer the handler.</summary>
public sealed class HandlerProbe
{
    private int _failuresRemaining;

    public ConcurrentQueue<(Guid WidgetId, int Attempt)> Invocations { get; } = new();
    public ConcurrentQueue<ActivityTraceId?> TraceIds { get; } = new();
    public bool AlwaysFail { get; set; }

    public void FailNext(int times) => _failuresRemaining = times;

    public bool ShouldFail() => AlwaysFail || Interlocked.Decrement(ref _failuresRemaining) >= 0;
}

public sealed class WidgetCreatedHandler(TestDbContext db, HandlerProbe probe) : IMessageHandler<WidgetCreated>
{
    public Task HandleAsync(WidgetCreated message, MessageContext context, CancellationToken cancellationToken)
    {
        probe.Invocations.Enqueue((message.WidgetId, context.Attempt));
        probe.TraceIds.Enqueue(Activity.Current?.TraceId);

        // The row is added before failing on purpose: a failure must roll it back.
        db.ProcessedWidgets.Add(new ProcessedWidget { Id = Guid.CreateVersion7(), WidgetId = message.WidgetId, Attempt = context.Attempt });
        if (probe.ShouldFail())
        {
            throw new InvalidOperationException("Simulated handler failure");
        }

        return Task.CompletedTask;
    }
}

/// <summary>A generic host running the messaging building block against real PostgreSQL and RabbitMQ.</summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private TestHost(IHost host, string prefix)
    {
        _host = host;
        Prefix = prefix;
    }

    public string Prefix { get; }
    public string Queue => Prefix + ".widgets";
    public string Exchange => Prefix + ".events";
    public string DeadLetterQueue => Prefix + ".dead-letters";
    public IServiceProvider Services => _host.Services;
    public HandlerProbe Probe => Services.GetRequiredService<HandlerProbe>();

    public static async Task<TestHost> StartAsync(string database, string rabbitMq, string? prefix = null, bool consume = true)
    {
        prefix ??= "t" + Guid.NewGuid().ToString("N")[..12];
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:rabbitmq"] = rabbitMq,
            ["Messaging:Exchange"] = prefix + ".events",
            ["Messaging:DeadLetterQueue"] = prefix + ".dead-letters",
            ["Messaging:RetryDelay"] = "00:00:00.200",
            ["Messaging:MaxAttempts"] = "3",
            ["Messaging:OutboxPollInterval"] = "00:00:00.050",
        });
        builder.Services.AddDbContext<TestDbContext>(options => options.UseNpgsql(database));
        builder.Services.AddSingleton<HandlerProbe>();
        builder.AddTicketHubMessaging<TestDbContext>(messaging =>
        {
            if (consume)
            {
                messaging.Consumes<WidgetCreated, WidgetCreatedHandler>(prefix + ".widgets");
            }
        });

        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync();
        }

        await host.StartAsync();
        return new TestHost(host, prefix);
    }

    public async Task<T> WithDbAsync<T>(Func<TestDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TestDbContext>());
    }

    /// <summary>Creates a widget and enqueues its event in one unit of work, as a real handler would.</summary>
    public async Task<Guid> CreateWidgetAsync(string name = "widget")
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var widget = new Widget { Id = Guid.CreateVersion7(), Name = name };
        db.Widgets.Add(widget);
        outbox.Enqueue(new WidgetCreated(widget.Id, name));
        await db.SaveChangesAsync();
        return widget.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
