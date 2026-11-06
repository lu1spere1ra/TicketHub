using System.Net;
using System.Net.Http.Json;
using TicketHub.TestKit;

namespace TicketHub.EndToEnd.Tests;

public sealed class HoldFlowTests(AppHostFixture system)
{
    [Fact]
    public async Task Customer_holds_seat_and_seat_map_shows_it_held()
    {
        var (api, _) = await system.NewCustomerAsync();
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
        var (alice, _) = await system.NewCustomerAsync();
        var (bob, _) = await system.NewCustomerAsync();
        var seat = (await alice.FreeSeatsAsync(Api.JazzNight, 1))[0];

        (await alice.HoldAsync(Api.JazzNight, seat)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await bob.HoldAsync(Api.JazzNight, seat)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Anonymous_visitor_can_browse_but_not_hold()
    {
        var anonymous = new Api(system.CreateGatewayClient());
        var seat = (await anonymous.FreeSeatsAsync(Api.JazzNight, 1))[0];

        (await anonymous.HoldAsync(Api.JazzNight, seat)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_sees_saga_state_through_the_gateway()
    {
        var (customer, _) = await system.NewCustomerAsync();
        var reservation = (await (await customer.HoldAsync(Api.JazzNight, (await customer.FreeSeatsAsync(Api.JazzNight, 1))))
            .Content.ReadFromJsonAsync<Api.Reservation>(TestContext.Current.CancellationToken))!;
        var admin = Api.WithToken(system.CreateGatewayClient(), await system.Keycloak.TokenForAsync("ops"));

        var saga = await admin.Http.GetAsync($"/admin/sagas/{reservation.Id}", TestContext.Current.CancellationToken);

        saga.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await customer.Http.GetAsync($"/admin/sagas/{reservation.Id}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
