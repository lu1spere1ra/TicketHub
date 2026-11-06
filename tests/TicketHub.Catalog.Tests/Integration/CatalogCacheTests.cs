using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Catalog.Data;
using TicketHub.Catalog.Features.Events;
using TicketHub.Contracts.Booking;
using TicketHub.TestKit;

namespace TicketHub.Catalog.Tests.Integration;

public sealed class CatalogCacheTests(PostgresContainer postgres, RabbitMqContainer rabbitMq, RedisContainer redis)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seat_layout_is_cached_but_availability_is_always_fresh()
    {
        await using var app = new CatalogApp(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString, redis.ConnectionString);
        await using var broker = await RawBroker.ConnectAsync(rabbitMq.ConnectionString);
        var client = app.CreateClient();
        var seat = (await SeatsAsync(client))[5];

        // Change the layout behind the cache's back: the cached layout keeps being served...
        await app.WithDbAsync(db => db.Seats.Where(s => s.Id == seat.SeatId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsBlocked, true), Ct));
        (await SeatsAsync(client)).Single(s => s.SeatId == seat.SeatId).Status.ShouldBe("Available");

        // ...while a status change from Booking shows up straight away, with no invalidation needed.
        await broker.PublishMessageAsync(app.Exchange, MessageTypeAttribute.NameOf<SeatAvailabilityChanged>(),
            new SeatAvailabilityChanged(DevelopmentSeed.JazzNightId, [new SeatStatusChange(seat.SeatId, "Held", 1)]));
        await Eventually.AssertAsync(async () => (await SeatsAsync(client)).Single(s => s.SeatId == seat.SeatId).Status.ShouldBe("Held"));
    }

    [Fact]
    public async Task Catalog_keeps_serving_when_redis_is_down()
    {
        var ownRedis = new RedisContainer();
        await ownRedis.InitializeAsync();
        await using var app = new CatalogApp(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString, ownRedis.ConnectionString);
        var client = app.CreateClient();
        (await client.GetAsync("/events", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await ownRedis.DisposeAsync();

        // The second-level cache is an optimisation: losing it costs database reads, not availability.
        (await client.GetAsync($"/events/{DevelopmentSeed.LoadTestNightId}/seat-map", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<List<SeatMapSeat>> SeatsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<SeatMapResponse>($"/events/{DevelopmentSeed.JazzNightId}/seat-map", Ct))!
            .Sections.SelectMany(s => s.Rows).SelectMany(r => r.Seats).ToList();
}
