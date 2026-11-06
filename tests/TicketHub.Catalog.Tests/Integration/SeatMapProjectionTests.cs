using System.Net.Http.Json;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Catalog.Data;
using TicketHub.Catalog.Features.Events;
using TicketHub.Contracts.Booking;
using TicketHub.TestKit;

namespace TicketHub.Catalog.Tests.Integration;

public sealed class SeatMapProjectionTests(PostgresContainer postgres, RabbitMqContainer rabbitMq) : IAsyncLifetime
{
    private static readonly string MessageType = MessageTypeAttribute.NameOf<SeatAvailabilityChanged>();
    private CatalogApp _app = null!;
    private HttpClient _client = null!;
    private RawBroker _broker = null!;

    public async ValueTask InitializeAsync()
    {
        _app = new CatalogApp(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString);
        _client = _app.CreateClient();
        _broker = await RawBroker.ConnectAsync(rabbitMq.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Seat_map_shows_seats_held_in_booking()
    {
        var seat = (await SeatsAsync())[5];

        await PublishAsync(new SeatStatusChange(seat.SeatId, "Held", 1));

        await Eventually.AssertAsync(async () => (await StatusOfAsync(seat.SeatId)).ShouldBe("Held"));
    }

    [Fact]
    public async Task Projection_applies_out_of_order_events_by_version()
    {
        var seats = await SeatsAsync();
        var seat = seats[5];
        var sentinel = seats[6];

        // Version 2 (Sold) overtakes version 1 (Held); the stale Held must not win.
        await PublishAsync(new SeatStatusChange(seat.SeatId, "Sold", 2));
        await PublishAsync(new SeatStatusChange(seat.SeatId, "Held", 1));
        await PublishAsync(new SeatStatusChange(sentinel.SeatId, "Held", 1));

        await Eventually.AssertAsync(async () => (await StatusOfAsync(sentinel.SeatId)).ShouldBe("Held"));
        (await StatusOfAsync(seat.SeatId)).ShouldBe("Sold");
    }

    private Task PublishAsync(SeatStatusChange change) =>
        _broker.PublishMessageAsync(_app.Exchange, MessageType, new SeatAvailabilityChanged(DevelopmentSeed.JazzNightId, [change]));

    private async Task<List<SeatMapSeat>> SeatsAsync() =>
        (await _client.GetFromJsonAsync<SeatMapResponse>($"/events/{DevelopmentSeed.JazzNightId}/seat-map", TestContext.Current.CancellationToken))!
            .Sections.SelectMany(s => s.Rows).SelectMany(r => r.Seats).ToList();

    private async Task<string> StatusOfAsync(Guid seatId) => (await SeatsAsync()).Single(s => s.SeatId == seatId).Status;
}
