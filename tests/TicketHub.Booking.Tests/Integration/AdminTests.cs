using System.Net.Http.Json;
using TicketHub.Booking.Features.Admin;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Contracts.Payments;
using TicketHub.TestKit;

namespace TicketHub.Booking.Tests.Integration;

public sealed class AdminTests(PostgresContainer postgres, RabbitMqContainer rabbitMq) : IAsyncLifetime
{
    private BookingApp _app = null!;

    public async ValueTask InitializeAsync()
    {
        _app = new BookingApp(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString);
        _ = _app.Server;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Saga_listing_filters_by_status()
    {
        var held = await _app.HoldOneAsync("a");
        var paying = await _app.CheckOutAsync((await _app.HoldOneAsync("b")).Id, "b");

        var page = await _app.CreateClient().GetFromJsonAsync<PagedResponse<SagaSummary>>("/admin/sagas?status=AwaitingPayment", Ct);

        page!.Items.Select(s => s.ReservationId).ShouldBe([paying.Id]);
        page.Items[0].LastStep.ShouldBe("PaymentRequested");
        held.Id.ShouldNotBe(paying.Id);
    }

    [Fact]
    public async Task Saga_detail_includes_timeline_in_order()
    {
        var reservation = await _app.CheckOutAsync((await _app.HoldOneAsync()).Id);

        var details = await _app.CreateClient().GetFromJsonAsync<SagaDetails>($"/admin/sagas/{reservation.Id}", Ct);

        details!.Timeline.Select(t => t.Step).ShouldBe(["Held", "PaymentRequested"]);
        details.Seats.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Dead_lettered_message_is_archived_with_error_details()
    {
        await using var broker = await RawBroker.ConnectAsync(rabbitMq.ConnectionString);

        // Unreadable payload on a queue Booking consumes: dead-lettered at once, then archived.
        await broker.PublishAsync(_app.Exchange, MessageTypeAttribute.NameOf<PaymentSucceeded>(), Guid.NewGuid(), "{oops");

        await Eventually.AssertAsync(async () =>
        {
            var page = await _app.CreateClient().GetFromJsonAsync<PagedResponse<DeadLetterResponse>>("/admin/dead-letters", Ct);
            var deadLetter = page!.Items.ShouldHaveSingleItem();
            deadLetter.OriginalQueue.ShouldBe(_app.Exchange + ".booking.payment-events");
            deadLetter.MessageType.ShouldBe(MessageTypeAttribute.NameOf<PaymentSucceeded>());
            deadLetter.ExceptionMessage!.ShouldContain("not a valid");
            deadLetter.Payload.ShouldBe("{oops");
        });
    }
}
