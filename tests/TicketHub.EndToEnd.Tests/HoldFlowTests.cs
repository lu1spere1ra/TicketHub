using System.Net;
using System.Net.Http.Json;
using TicketHub.TestKit;

namespace TicketHub.EndToEnd.Tests;

public sealed class HoldFlowTests(AppHostFixture system)
{
    [Fact]
    public async Task Customer_holds_seat_and_seat_map_shows_it_held()
    {
        var api = Api.ForCustomer(system.CreateGatewayClient(), "e2e-hold");
        var seat = (await api.FreeSeatsAsync(Api.JazzNight, 1))[0];

        var response = await api.HoldAsync(Api.JazzNight, seat);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var reservation = (await response.Content.ReadFromJsonAsync<Api.Reservation>(TestContext.Current.CancellationToken))!;
        reservation.Status.ShouldBe("Held");
        // Booking -> outbox -> RabbitMQ -> Catalog projection: eventually consistent, so poll.
        await Eventually.AssertAsync(async () => (await api.SeatStatusAsync(Api.JazzNight, seat)).ShouldBe("Held"));
    }

    [Fact]
    public async Task Second_customer_cannot_hold_a_seat_already_held()
    {
        var alice = Api.ForCustomer(system.CreateGatewayClient(), "e2e-alice");
        var bob = Api.ForCustomer(system.CreateGatewayClient(), "e2e-bob");
        var seat = (await alice.FreeSeatsAsync(Api.JazzNight, 1))[0];

        (await alice.HoldAsync(Api.JazzNight, seat)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await bob.HoldAsync(Api.JazzNight, seat)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
