using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketHub.Booking.Data;
using TicketHub.Booking.Domain;
using TicketHub.Booking.Features.Checkout;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Contracts.Booking;
using TicketHub.Contracts.Payments;
using TicketHub.TestKit;

namespace TicketHub.Booking.Tests.Integration;

public sealed class CheckoutSagaTests(PostgresContainer postgres, RabbitMqContainer rabbitMq) : IAsyncLifetime
{
    private BookingApp _app = null!;
    private RawBroker _broker = null!;

    public async ValueTask InitializeAsync()
    {
        _app = new BookingApp(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString);
        _ = _app.Server;
        _broker = await RawBroker.ConnectAsync(rabbitMq.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        await _app.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Checkout_requests_payment_and_moves_to_awaiting_payment()
    {
        var hold = await _app.HoldOneAsync();

        var response = await _app.CreateCustomer("alice").PostAsJsonAsync($"/reservations/{hold.Id}/checkout", new { paymentToken = "tok" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await StatusAsync(hold.Id)).ShouldBe(ReservationStatus.AwaitingPayment);
        var request = await OutboxAsync<RequestPayment>();
        request.ShouldHaveSingleItem().Payload.ShouldContain("\"amount\": 50");
    }

    [Fact]
    public async Task Checkout_after_deadline_returns_410()
    {
        var hold = await _app.HoldOneAsync();
        _app.Clock.Advance(TimeSpan.FromMinutes(10));

        var response = await _app.CreateCustomer("alice").PostAsJsonAsync($"/reservations/{hold.Id}/checkout", new { paymentToken = "tok" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Customer_cannot_check_out_another_customers_reservation()
    {
        var hold = await _app.HoldOneAsync("alice");

        var response = await _app.CreateCustomer("mallory").PostAsJsonAsync($"/reservations/{hold.Id}/checkout", new { paymentToken = "tok" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Payment_success_confirms_reservation_and_sells_seats()
    {
        var hold = await _app.CheckOutAsync((await _app.HoldOneAsync()).Id);

        await PublishAsync(new PaymentSucceeded(Guid.NewGuid(), hold.Id, 50m, "EUR", DateTimeOffset.UtcNow));

        await Eventually.AssertAsync(async () => (await StatusAsync(hold.Id)).ShouldBe(ReservationStatus.Confirmed));
        (await SeatStatusesAsync(hold.Id)).ShouldAllBe(s => s == AllocationStatus.Sold);
        (await OutboxAsync<ReservationConfirmed>()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Payment_failure_cancels_reservation_and_releases_seats()
    {
        var hold = await _app.CheckOutAsync((await _app.HoldOneAsync()).Id);

        await PublishAsync(new PaymentFailed(Guid.NewGuid(), hold.Id, "card declined", DateTimeOffset.UtcNow));

        await Eventually.AssertAsync(async () => (await StatusAsync(hold.Id)).ShouldBe(ReservationStatus.Cancelled));
        (await ReleasedSeatsAsync(hold)).ShouldAllBe(s => s.Status == AllocationStatus.Available && s.ReservationId == null);
        (await OutboxAsync<ReservationCancelled>()).ShouldHaveSingleItem().Payload.ShouldContain("card declined");
    }

    [Fact]
    public async Task Expired_hold_releases_seats_and_publishes_ReservationExpired()
    {
        var hold = await _app.HoldOneAsync();

        _app.Clock.Advance(TimeSpan.FromMinutes(10));
        await ExpireAsync();

        // The background service may have claimed the row first (SKIP LOCKED); wait for whichever pass commits.
        await Eventually.AssertAsync(async () => (await StatusAsync(hold.Id)).ShouldBe(ReservationStatus.Expired));
        (await ReleasedSeatsAsync(hold)).ShouldAllBe(s => s.Status == AllocationStatus.Available);
        (await OutboxAsync<ReservationExpired>()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Hold_is_not_expired_before_its_deadline()
    {
        var hold = await _app.HoldOneAsync();

        _app.Clock.Advance(TimeSpan.FromMinutes(9));
        await ExpireAsync();

        (await StatusAsync(hold.Id)).ShouldBe(ReservationStatus.Held);
    }

    [Fact]
    public async Task Late_payment_after_expiry_requests_a_refund_and_seats_stay_released()
    {
        // RF-2
        var hold = await _app.CheckOutAsync((await _app.HoldOneAsync()).Id);
        _app.Clock.Advance(TimeSpan.FromMinutes(10));
        await ExpireAsync();
        await Eventually.AssertAsync(async () => (await StatusAsync(hold.Id)).ShouldBe(ReservationStatus.Expired));
        var paymentId = Guid.NewGuid();

        await PublishAsync(new PaymentSucceeded(paymentId, hold.Id, 50m, "EUR", DateTimeOffset.UtcNow));

        await Eventually.AssertAsync(async () => (await OutboxAsync<RefundPayment>()).ShouldHaveSingleItem().Payload.ShouldContain(paymentId.ToString()));
        (await StatusAsync(hold.Id)).ShouldBe(ReservationStatus.Expired);
        (await ReleasedSeatsAsync(hold)).ShouldAllBe(s => s.Status == AllocationStatus.Available);
    }

    [Fact]
    public async Task Concurrent_expiry_and_payment_success_end_in_one_consistent_state()
    {
        // RF-2: the expiry job and the payment handler race for the same reservation. Whichever commits first wins
        // and the other re-reads. Valid outcomes: Confirmed with sold seats, or Expired with a refund and free seats.
        var reservations = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            reservations.Add((await _app.CheckOutAsync((await _app.HoldOneAsync($"c{i}")).Id, $"c{i}")).Id);
        }

        _app.Clock.Advance(TimeSpan.FromMinutes(10));
        var payments = reservations.Select(id => PublishAsync(new PaymentSucceeded(Guid.NewGuid(), id, 50m, "EUR", DateTimeOffset.UtcNow)));
        await Task.WhenAll(payments.Append(ExpireAsync()));

        foreach (var id in reservations)
        {
            await Eventually.AssertAsync(async () =>
            {
                var reservation = await _app.WithDbAsync(db => db.Reservations.AsNoTracking().SingleAsync(r => r.Id == id, Ct));
                var seats = await SeatStatusesAsync(id);
                if (reservation.Status == ReservationStatus.Confirmed)
                {
                    seats.ShouldAllBe(s => s == AllocationStatus.Sold);
                    reservation.RefundedPaymentId.ShouldBeNull();
                }
                else
                {
                    reservation.Status.ShouldBe(ReservationStatus.Expired);
                    reservation.RefundedPaymentId.ShouldNotBeNull();
                    seats.ShouldBeEmpty(); // released seats no longer point at the reservation
                }
            });
        }
    }

    [Fact]
    public async Task Two_expiry_workers_expire_each_reservation_once()
    {
        for (var i = 0; i < 30; i++)
        {
            await _app.HoldOneAsync($"c{i}");
        }

        _app.Clock.Advance(TimeSpan.FromMinutes(10));
        // Three explicit passes race the background service (whose timer also fired when the clock moved).
        await Task.WhenAll(ExpireAsync(), ExpireAsync(), ExpireAsync());

        await Eventually.AssertAsync(async () => (await OutboxAsync<ReservationExpired>()).Count.ShouldBe(30));
        await Task.Delay(500, Ct);
        (await OutboxAsync<ReservationExpired>()).Count.ShouldBe(30);
    }

    private Task ExpireAsync() => _app.Services.GetRequiredService<HoldExpiryService>().ExpireBatchAsync(Ct);

    private Task PublishAsync<T>(T message) where T : IIntegrationMessage =>
        _broker.PublishMessageAsync(_app.Exchange, MessageTypeAttribute.NameOf(typeof(T)), message);

    private Task<ReservationStatus> StatusAsync(Guid id) =>
        _app.WithDbAsync(db => db.Reservations.AsNoTracking().Where(r => r.Id == id).Select(r => r.Status).SingleAsync(Ct));

    private Task<List<AllocationStatus>> SeatStatusesAsync(Guid reservationId) =>
        _app.WithDbAsync(db => db.SeatAllocations.AsNoTracking().Where(a => a.ReservationId == reservationId).Select(a => a.Status).ToListAsync(Ct));

    private Task<List<SeatAllocation>> ReleasedSeatsAsync(Features.Reservations.ReservationResponse reservation)
    {
        var seatIds = reservation.Seats.Select(s => s.SeatId).ToList();
        return _app.WithDbAsync(db => db.SeatAllocations.AsNoTracking().Where(a => seatIds.Contains(a.SeatId)).ToListAsync(Ct));
    }

    private Task<List<OutboxMessage>> OutboxAsync<T>() where T : IIntegrationMessage
    {
        var type = MessageTypeAttribute.NameOf(typeof(T));
        return _app.WithDbAsync(db => db.Set<OutboxMessage>().AsNoTracking().Where(m => m.Type == type).ToListAsync(Ct));
    }
}
