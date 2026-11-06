using System.Net;
using System.Net.Http.Json;
using TicketHub.BuildingBlocks;
using TicketHub.Catalog.Data;
using TicketHub.Catalog.Domain;
using TicketHub.Catalog.Features.Events;
using TicketHub.Catalog.Grpc;
using TicketHub.TestKit;

namespace TicketHub.Catalog.Tests.Integration;

public sealed class CatalogApiTests(PostgresContainer postgres, RabbitMqContainer rabbitMq, RedisContainer redis) : IAsyncLifetime
{
    private CatalogApp _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _app = new CatalogApp(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString, redis.ConnectionString);
        _client = _app.CreateClient();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task List_events_returns_events_ordered_by_start_date()
    {
        var page = await _client.GetFromJsonAsync<PagedResponse<EventSummary>>("/events", TestContext.Current.CancellationToken);

        page!.TotalCount.ShouldBe(2);
        page.Items.Select(e => e.Name).ShouldBe(["Midnight Jazz Session", "Load Test Night"]);
        page.Items.ShouldAllBe(e => e.OnSale);
    }

    [Fact]
    public async Task Unknown_event_returns_404()
    {
        var response = await _client.GetAsync($"/events/{Guid.NewGuid()}/seat-map", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Seat_map_returns_layout_with_availability()
    {
        var map = await GetSeatMapAsync(DevelopmentSeed.JazzNightId);

        map.Sections.Select(s => (s.Name, s.Price)).ShouldBe([("A", 45m), ("B", 30m)]);
        map.Sections.SelectMany(s => s.Rows).SelectMany(r => r.Seats).Count().ShouldBe(100);
        var firstRow = map.Sections[0].Rows[0];
        firstRow.Row.ShouldBe("1");
        firstRow.Seats.Select(s => s.Number).ShouldBe(Enumerable.Range(1, 10));
        firstRow.Seats[0].Status.ShouldBe("Blocked");
        firstRow.Seats.Skip(1).ShouldAllBe(s => s.Status == "Available");
    }

    [Fact]
    public async Task CheckSeats_returns_prices_and_flags_unknown_and_blocked_seats()
    {
        var map = await GetSeatMapAsync(DevelopmentSeed.JazzNightId);
        var blocked = map.Sections[0].Rows[0].Seats[0];
        var sectionA = map.Sections[0].Rows[0].Seats[1];
        var sectionB = map.Sections[1].Rows[0].Seats[0];
        var unknown = Guid.NewGuid().ToString();

        var response = await _app.CreateGrpcClient().CheckSeatsAsync(new CheckSeatsRequest
        {
            EventId = DevelopmentSeed.JazzNightId.ToString(),
            SeatIds = { sectionA.SeatId.ToString(), sectionB.SeatId.ToString(), blocked.SeatId.ToString(), unknown },
        }, cancellationToken: TestContext.Current.CancellationToken);

        response.EventFound.ShouldBeTrue();
        response.EventOnSale.ShouldBeTrue();
        response.Seats.Select(s => (s.Exists, s.Sellable, s.PriceMinor, s.Label)).ShouldBe(
        [
            (true, true, 4500L, sectionA.Label),
            (true, true, 3000L, sectionB.Label),
            (true, false, 4500L, blocked.Label),
            (false, false, 0L, ""),
        ]);
        response.Seats[3].SeatId.ShouldBe(unknown);
    }

    [Fact]
    public async Task CheckSeats_reports_event_not_on_sale_outside_sales_window()
    {
        var eventId = Guid.NewGuid();
        await _app.WithDbAsync(async db =>
        {
            var venue = db.Venues.First();
            var now = DateTimeOffset.UtcNow;
            db.Events.Add(Event.Schedule(eventId, "Not yet", venue, now.AddDays(10), now.AddDays(1), now.AddDays(9),
                [new SectionPrice("A", 10m, Money.Euro)]));
            return await db.SaveChangesAsync();
        });

        var response = await _app.CreateGrpcClient().CheckSeatsAsync(
            new CheckSeatsRequest { EventId = eventId.ToString() }, cancellationToken: TestContext.Current.CancellationToken);

        response.EventFound.ShouldBeTrue();
        response.EventOnSale.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckSeats_requires_an_authenticated_caller()
    {
        var call = () => _app.CreateGrpcClient(authenticated: false).CheckSeatsAsync(
            new CheckSeatsRequest { EventId = DevelopmentSeed.JazzNightId.ToString() }, cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;

        (await Should.ThrowAsync<global::Grpc.Core.RpcException>(call)).StatusCode.ShouldBe(global::Grpc.Core.StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task CheckSeats_reports_unknown_event()
    {
        var response = await _app.CreateGrpcClient().CheckSeatsAsync(
            new CheckSeatsRequest { EventId = Guid.NewGuid().ToString() }, cancellationToken: TestContext.Current.CancellationToken);

        response.EventFound.ShouldBeFalse();
    }

    private async Task<SeatMapResponse> GetSeatMapAsync(Guid eventId) =>
        (await _client.GetFromJsonAsync<SeatMapResponse>($"/events/{eventId}/seat-map", TestContext.Current.CancellationToken))!;
}
