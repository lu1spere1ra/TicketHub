using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.TestKit;

namespace TicketHub.BuildingBlocks.Tests.Messaging;

public class OutboxAndInboxTests(PostgresContainer postgres, RabbitMqContainer rabbitMq)
{
    private static readonly string WidgetCreatedType = MessageTypeAttribute.NameOf<WidgetCreated>();

    [Fact]
    public async Task Enqueued_message_is_published_after_commit()
    {
        await using var host = await TestHost.StartAsync(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString);

        var widgetId = await host.CreateWidgetAsync();

        await Eventually.AssertAsync(async () =>
            (await host.WithDbAsync(db => db.ProcessedWidgets.CountAsync(p => p.WidgetId == widgetId))).ShouldBe(1));
        (await host.WithDbAsync(db => db.Set<OutboxMessage>().SingleAsync())).SentAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Enqueued_message_is_not_published_when_transaction_rolls_back()
    {
        await using var host = await TestHost.StartAsync(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            scope.ServiceProvider.GetRequiredService<IOutbox>().Enqueue(new WidgetCreated(Guid.NewGuid(), "ghost"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        // Prove the pipeline is alive by publishing a real message after the rolled-back one.
        var realId = await host.CreateWidgetAsync();
        await Eventually.AssertAsync(() => host.Probe.Invocations.ShouldContain(i => i.WidgetId == realId));
        host.Probe.Invocations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Duplicate_delivery_invokes_handler_once()
    {
        await using var host = await TestHost.StartAsync(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString);
        await using var broker = await RawBroker.ConnectAsync(rabbitMq.ConnectionString);
        var messageId = Guid.CreateVersion7();
        var widgetId = Guid.NewGuid();
        var body = $$"""{"widgetId":"{{widgetId}}","name":"dup"}""";

        // Same message id twice (what a relay crash between publish and commit produces), then a sentinel.
        // Deliveries on one channel are handled in order, so once the sentinel is processed both copies were seen.
        await broker.PublishAsync(host.Exchange, WidgetCreatedType, messageId, body);
        await broker.PublishAsync(host.Exchange, WidgetCreatedType, messageId, body);
        var sentinel = Guid.NewGuid();
        await broker.PublishAsync(host.Exchange, WidgetCreatedType, Guid.CreateVersion7(), $$"""{"widgetId":"{{sentinel}}","name":"s"}""");

        await Eventually.AssertAsync(() => host.Probe.Invocations.ShouldContain(i => i.WidgetId == sentinel));
        host.Probe.Invocations.Count(i => i.WidgetId == widgetId).ShouldBe(1);
        (await host.WithDbAsync(db => db.ProcessedWidgets.CountAsync(p => p.WidgetId == widgetId))).ShouldBe(1);
    }

    [Fact]
    public async Task Handler_exception_rolls_back_handler_changes_and_inbox_row()
    {
        await using var host = await TestHost.StartAsync(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString);
        host.Probe.FailNext(1);

        var widgetId = await host.CreateWidgetAsync();

        // Had the first attempt's row or inbox entry survived, the retry would have been skipped as a duplicate
        // and the stored attempt would be 1.
        await Eventually.AssertAsync(async () =>
        {
            var rows = await host.WithDbAsync(db => db.ProcessedWidgets.Where(p => p.WidgetId == widgetId).ToListAsync());
            rows.ShouldHaveSingleItem().Attempt.ShouldBe(2);
        });
    }

    [Fact]
    public async Task Failing_handler_is_retried_then_dead_lettered_with_error_details()
    {
        await using var host = await TestHost.StartAsync(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString);
        await using var broker = await RawBroker.ConnectAsync(rabbitMq.ConnectionString);
        host.Probe.AlwaysFail = true;

        var widgetId = await host.CreateWidgetAsync();

        RabbitMQ.Client.BasicGetResult? deadLetter = null;
        await Eventually.AssertAsync(async () => (deadLetter = await broker.GetAsync(host.DeadLetterQueue)).ShouldNotBeNull());

        host.Probe.Invocations.Where(i => i.WidgetId == widgetId).Select(i => i.Attempt).ShouldBe([1, 2, 3]);
        var headers = deadLetter!.BasicProperties.Headers!;
        Encoding.UTF8.GetString((byte[])headers[MessageHeaders.OriginalQueue]!).ShouldBe(host.Queue);
        Encoding.UTF8.GetString((byte[])headers[MessageHeaders.ExceptionType]!).ShouldBe(typeof(InvalidOperationException).FullName);
        Encoding.UTF8.GetString((byte[])headers[MessageHeaders.ExceptionMessage]!).ShouldBe("Simulated handler failure");
        headers[MessageHeaders.Attempt].ShouldBe(3);
        (await host.WithDbAsync(db => db.ProcessedWidgets.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task Handler_succeeding_on_a_later_attempt_is_not_dead_lettered()
    {
        await using var host = await TestHost.StartAsync(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString);
        await using var broker = await RawBroker.ConnectAsync(rabbitMq.ConnectionString);
        host.Probe.FailNext(2);

        var widgetId = await host.CreateWidgetAsync();

        await Eventually.AssertAsync(async () =>
            (await host.WithDbAsync(db => db.ProcessedWidgets.SingleAsync(p => p.WidgetId == widgetId))).Attempt.ShouldBe(3));
        (await broker.GetAsync(host.DeadLetterQueue)).ShouldBeNull();
    }

    [Fact]
    public async Task Undeserialisable_message_is_dead_lettered_without_retry()
    {
        await using var host = await TestHost.StartAsync(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString);
        await using var broker = await RawBroker.ConnectAsync(rabbitMq.ConnectionString);

        await broker.PublishAsync(host.Exchange, WidgetCreatedType, Guid.CreateVersion7(), "{not json");

        RabbitMQ.Client.BasicGetResult? deadLetter = null;
        await Eventually.AssertAsync(async () => (deadLetter = await broker.GetAsync(host.DeadLetterQueue)).ShouldNotBeNull());
        deadLetter!.BasicProperties.Headers![MessageHeaders.Attempt].ShouldBe(1);
        host.Probe.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Relay_instances_do_not_publish_the_same_row_twice()
    {
        var database = await postgres.CreateDatabaseAsync();
        var prefix = "t" + Guid.NewGuid().ToString("N")[..12];
        await using var broker = await RawBroker.ConnectAsync(rabbitMq.ConnectionString);
        var seen = await broker.ObserveAsync(prefix + ".events", WidgetCreatedType);

        // Rows are written while no relay runs, then two relays race over the same table.
        await using (var writer = await TestHost.StartAsync(database, rabbitMq.ConnectionString, prefix + "w", consume: false))
        {
            await writer.WithDbAsync(async db =>
            {
                for (var i = 0; i < 200; i++)
                {
                    db.Set<OutboxMessage>().Add(new OutboxMessage
                    {
                        Id = Guid.CreateVersion7(),
                        Type = WidgetCreatedType,
                        Payload = $$"""{"widgetId":"{{Guid.NewGuid()}}","name":"n"}""",
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                }

                return await db.SaveChangesAsync();
            });
        }

        await using var relayA = await TestHost.StartAsync(database, rabbitMq.ConnectionString, prefix, consume: false);
        await using var relayB = await TestHost.StartAsync(database, rabbitMq.ConnectionString, prefix, consume: false);

        await Eventually.AssertAsync(() => seen.Count.ShouldBeGreaterThanOrEqualTo(200));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        seen.Count.ShouldBe(200);
        seen.Distinct().Count().ShouldBe(200);
    }

    [Fact]
    public async Task Trace_context_flows_from_enqueue_to_handler()
    {
        using var source = new ActivitySource("TicketHub.Tests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name.StartsWith("TicketHub.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        await using var host = await TestHost.StartAsync(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString);

        ActivityTraceId requestTraceId;
        Guid widgetId;
        using (var request = source.StartActivity("simulated request")!)
        {
            requestTraceId = request.TraceId;
            widgetId = await host.CreateWidgetAsync();
        }

        await Eventually.AssertAsync(() => host.Probe.Invocations.ShouldContain(i => i.WidgetId == widgetId));
        host.Probe.TraceIds.ShouldHaveSingleItem().ShouldBe(requestTraceId);
    }
}
