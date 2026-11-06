using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TicketHub.Booking.Domain;
using TicketHub.Booking.Features.Reservations;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Contracts.Booking;
using TicketHub.TestKit;

namespace TicketHub.Booking.Tests.Integration;

public sealed class HoldSeatsTests(PostgresContainer postgres, RabbitMqContainer rabbitMq) : IAsyncLifetime
{
    private BookingApp _app = null!;

    public async ValueTask InitializeAsync()
    {
        _app = new BookingApp(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString);
        _ = _app.Server; // start the host
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hold_returns_201_with_reservation_and_expiry()
    {
        var (eventId, seats) = _app.Catalog.AddEvent();

        var response = await BookingApp.HoldAsync(_app.CreateCustomer("alice"), eventId, seats[0], seats[1]);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var reservation = (await response.Content.ReadFromJsonAsync<ReservationResponse>(Ct))!;
        reservation.Status.ShouldBe("Held");
        reservation.Total.ShouldBe(50m);
        reservation.Seats.Select(s => s.SeatId).ShouldBe([seats[0], seats[1]], ignoreOrder: true);
        reservation.HoldExpiresAt.ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(10));
        response.Headers.Location!.ToString().ShouldBe($"/reservations/{reservation.Id}");
    }

    [Fact]
    public async Task Parallel_holds_for_same_seat_produce_exactly_one_reservation()
    {
        var (eventId, seats) = _app.Catalog.AddEvent();
        var customers = Enumerable.Range(0, 100).Select(i => _app.CreateCustomer($"customer-{i}")).ToList();

        var responses = await Task.WhenAll(customers.Select(c => BookingApp.HoldAsync(c, eventId, seats[0])));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(99);
        var allocation = await _app.WithDbAsync(db => db.SeatAllocations.SingleAsync(a => a.SeatId == seats[0], Ct));
        allocation.Status.ShouldBe(AllocationStatus.Held);
        (await _app.WithDbAsync(db => db.Reservations.CountAsync(Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Overlapping_multi_seat_holds_in_opposite_order_are_all_or_nothing()
    {
        // RF-3: A asks for [s1, s2] while B asks for [s2, s1]. Exactly one must win both seats, with no deadlock
        // surfacing as a 500 and never one seat each.
        for (var round = 0; round < 25; round++)
        {
            var (eventId, seats) = _app.Catalog.AddEvent(seats: 2);
            var a = _app.CreateCustomer("a");
            var b = _app.CreateCustomer("b");

            var responses = await Task.WhenAll(
                BookingApp.HoldAsync(a, eventId, seats[0], seats[1]),
                BookingApp.HoldAsync(b, eventId, seats[1], seats[0]));

            responses.Select(r => r.StatusCode).ShouldBe([HttpStatusCode.Created, HttpStatusCode.Conflict], ignoreOrder: true);
            var holders = await _app.WithDbAsync(db => db.SeatAllocations
                .Where(s => s.EventId == eventId)
                .Select(s => s.ReservationId)
                .Distinct()
                .ToListAsync(Ct));
            holders.ShouldHaveSingleItem().ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task Hold_writes_SeatAvailabilityChanged_to_outbox_in_same_transaction()
    {
        var (eventId, seats) = _app.Catalog.AddEvent();

        var reservation = await (await BookingApp.HoldAsync(_app.CreateCustomer("alice"), eventId, seats[0]))
            .Content.ReadFromJsonAsync<ReservationResponse>(Ct);

        var outbox = await _app.WithDbAsync(db => db.Set<OutboxMessage>().SingleAsync(Ct));
        outbox.Type.ShouldBe(MessageTypeAttribute.NameOf<SeatAvailabilityChanged>());
        outbox.CorrelationId.ShouldBe(reservation!.Id);
        outbox.Payload.ShouldContain(seats[0].ToString());
        outbox.Payload.ShouldContain("\"status\": \"Held\"", Case.Insensitive);
    }

    [Fact]
    public async Task Seat_held_by_someone_else_returns_409_listing_the_seat()
    {
        var (eventId, seats) = _app.Catalog.AddEvent();
        await BookingApp.HoldAsync(_app.CreateCustomer("alice"), eventId, seats[0]);

        var response = await BookingApp.HoldAsync(_app.CreateCustomer("bob"), eventId, seats[1], seats[0]);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadAsStringAsync(Ct);
        problem.ShouldContain("seat-unavailable");
        problem.ShouldContain(seats[0].ToString());
        // All or nothing: the free seat in the failed request was not held either.
        (await _app.WithDbAsync(db => db.SeatAllocations.SingleAsync(a => a.SeatId == seats[1], Ct))).Status.ShouldBe(AllocationStatus.Available);
    }

    [Fact]
    public async Task Hold_returns_404_for_unknown_seat()
    {
        var (eventId, _) = _app.Catalog.AddEvent();

        var response = await BookingApp.HoldAsync(_app.CreateCustomer("alice"), eventId, Guid.NewGuid());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Hold_returns_404_for_unknown_event()
    {
        var response = await BookingApp.HoldAsync(_app.CreateCustomer("alice"), Guid.NewGuid(), Guid.NewGuid());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Hold_returns_422_when_event_not_on_sale()
    {
        var (eventId, seats) = _app.Catalog.AddEvent(onSale: false);

        var response = await BookingApp.HoldAsync(_app.CreateCustomer("alice"), eventId, seats[0]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Hold_returns_422_for_a_blocked_seat()
    {
        var (eventId, seats) = _app.Catalog.AddEvent();
        _app.Catalog.Unsellable[seats[0]] = true;

        var response = await BookingApp.HoldAsync(_app.CreateCustomer("alice"), eventId, seats[0]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Hold_returns_400_for_more_than_six_seats()
    {
        var (eventId, seats) = _app.Catalog.AddEvent();

        var response = await BookingApp.HoldAsync(_app.CreateCustomer("alice"), eventId, seats.Take(7).ToArray());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Hold_returns_503_when_catalog_is_unavailable()
    {
        var (eventId, seats) = _app.Catalog.AddEvent();
        _app.Catalog.Unavailable = true;

        var response = await BookingApp.HoldAsync(_app.CreateCustomer("alice"), eventId, seats[0]);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Token_for_another_audience_is_rejected()
    {
        var (eventId, seats) = _app.Catalog.AddEvent();
        var client = _app.CreateClient().WithBearer(TestJwt.Issue("alice", ["customer"], audience: "some-other-api"));

        (await BookingApp.HoldAsync(client, eventId, seats[0])).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_without_customer_role_cannot_hold_seats()
    {
        var (eventId, seats) = _app.Catalog.AddEvent();

        (await BookingApp.HoldAsync(_app.CreateAdmin(), eventId, seats[0])).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Request_without_token_returns_401()
    {
        var (eventId, seats) = _app.Catalog.AddEvent();

        var response = await BookingApp.HoldAsync(_app.CreateClient(), eventId, seats[0]);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Customer_cannot_read_another_customers_reservation()
    {
        var (eventId, seats) = _app.Catalog.AddEvent();
        var reservation = await (await BookingApp.HoldAsync(_app.CreateCustomer("alice"), eventId, seats[0]))
            .Content.ReadFromJsonAsync<ReservationResponse>(Ct);

        (await _app.CreateCustomer("alice").GetAsync($"/reservations/{reservation!.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.CreateCustomer("mallory").GetAsync($"/reservations/{reservation.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
