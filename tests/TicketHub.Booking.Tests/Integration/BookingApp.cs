using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TicketHub.Booking.Catalog;
using TicketHub.Booking.Data;
using TicketHub.Booking.Features.Reservations;

namespace TicketHub.Booking.Tests.Integration;

/// <summary>The real Booking host on a fresh database, with Catalog replaced by <see cref="FakeCatalog"/>.</summary>
public sealed class BookingApp(string database, string rabbitMq) : WebApplicationFactory<Program>
{
    public FakeCatalog Catalog { get; } = new();

    /// <summary>Business time is frozen; tests move it explicitly (e.g. past a hold's deadline).</summary>
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public string Exchange { get; } = "t" + Guid.NewGuid().ToString("N")[..12] + ".events";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:bookingdb", database);
        builder.UseSetting("ConnectionStrings:rabbitmq", rabbitMq);
        builder.UseSetting("Messaging:Exchange", Exchange);
        builder.UseSetting("Messaging:DeadLetterQueue", Exchange + ".dead-letters");
        builder.UseSetting("Messaging:QueuePrefix", Exchange + ".");
        builder.UseSetting("Messaging:RetryDelay", "00:00:00.200");
        builder.UseSetting("Messaging:OutboxPollInterval", "00:00:00.050");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ICatalogSeatChecker>(Catalog);
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public HttpClient CreateCustomer(string userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Email", $"{userId}@example.com");
        return client;
    }

    public static Task<HttpResponseMessage> HoldAsync(HttpClient client, Guid eventId, params Guid[] seatIds) =>
        client.PostAsJsonAsync("/reservations", new HoldSeatsRequest(eventId, seatIds));

    public async Task<ReservationResponse> HoldOneAsync(string customer = "alice")
    {
        var (eventId, seats) = Catalog.AddEvent(seats: 2);
        var response = await HoldAsync(CreateCustomer(customer), eventId, seats[0], seats[1]);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReservationResponse>())!;
    }

    public async Task<ReservationResponse> CheckOutAsync(Guid reservationId, string customer = "alice")
    {
        var response = await CreateCustomer(customer).PostAsJsonAsync($"/reservations/{reservationId}/checkout", new { paymentToken = "test-card-approve" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReservationResponse>())!;
    }

    public async Task<T> WithDbAsync<T>(Func<BookingDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<BookingDbContext>());
    }
}
