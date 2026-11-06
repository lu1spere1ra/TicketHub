using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TicketHub.TestKit;

namespace TicketHub.EndToEnd.Tests;

/// <summary>The whole saga across Booking, Payments, Catalog and Notifications, through the gateway only.</summary>
public sealed class PurchaseFlowTests(AppHostFixture system)
{
    private static readonly TimeSpan SagaTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Customer_completes_purchase_and_receives_confirmation_email()
    {
        var (api, email) = NewCustomer();
        var seats = await api.FreeSeatsAsync(Api.JazzNight, 2);
        var reservation = await HoldAsync(api, seats);

        (await api.CheckoutAsync(reservation.Id, "test-card-approve")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await Eventually.AssertAsync(async () => (await api.GetReservationAsync(reservation.Id)).Status.ShouldBe("Confirmed"), SagaTimeout);
        await Eventually.AssertAsync(async () =>
        {
            foreach (var seat in seats)
            {
                (await api.SeatStatusAsync(Api.JazzNight, seat)).ShouldBe("Sold");
            }
        }, SagaTimeout);
        await Eventually.AssertAsync(async () => (await SubjectsSentToAsync(email)).ShouldContain("Your tickets are confirmed"), SagaTimeout);
    }

    [Fact]
    public async Task Declined_payment_releases_seats_and_notifies_customer()
    {
        var (api, email) = NewCustomer();
        var seat = (await api.FreeSeatsAsync(Api.JazzNight, 1))[0];
        var reservation = await HoldAsync(api, [seat]);

        await api.CheckoutAsync(reservation.Id, "test-card-decline");

        await Eventually.AssertAsync(async () => (await api.GetReservationAsync(reservation.Id)).Status.ShouldBe("Cancelled"), SagaTimeout);
        await Eventually.AssertAsync(async () => (await api.SeatStatusAsync(Api.JazzNight, seat)).ShouldBe("Available"), SagaTimeout);
        await Eventually.AssertAsync(async () => (await SubjectsSentToAsync(email)).ShouldContain("Your payment did not go through"), SagaTimeout);
    }

    [Fact]
    public async Task Unpaid_hold_expires_and_seats_become_available()
    {
        var (api, email) = NewCustomer();
        var seat = (await api.FreeSeatsAsync(Api.JazzNight, 1))[0];
        var reservation = await HoldAsync(api, [seat]);

        // Holds last 5 seconds in this test run.
        await Eventually.AssertAsync(async () => (await api.GetReservationAsync(reservation.Id)).Status.ShouldBe("Expired"), SagaTimeout);
        await Eventually.AssertAsync(async () => (await api.SeatStatusAsync(Api.JazzNight, seat)).ShouldBe("Available"), SagaTimeout);
        await Eventually.AssertAsync(async () => (await SubjectsSentToAsync(email)).ShouldContain("Your seat reservation expired"), SagaTimeout);
    }

    private (Api Api, string Email) NewCustomer()
    {
        var userId = "e2e-" + Guid.NewGuid().ToString("N")[..8];
        return (Api.ForCustomer(system.CreateGatewayClient(), userId), $"{userId}@example.com");
    }

    private static async Task<Api.Reservation> HoldAsync(Api api, Guid[] seats)
    {
        var response = await api.HoldAsync(Api.JazzNight, seats);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<Api.Reservation>())!;
    }

    private async Task<List<string>> SubjectsSentToAsync(string address)
    {
        using var mailpit = system.CreateMailpitClient();
        var result = await mailpit.GetFromJsonAsync<JsonElement>($"/api/v1/search?query=to:{address}");
        return result.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("Subject").GetString()!).ToList();
    }
}
