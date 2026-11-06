using Microsoft.EntityFrameworkCore;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Contracts.Payments;
using TicketHub.Payments.Domain;
using TicketHub.Payments.Provider;
using TicketHub.TestKit;

namespace TicketHub.Payments.Tests.Integration;

public sealed class PaymentProcessingTests(PostgresContainer postgres, RabbitMqContainer rabbitMq) : IAsyncLifetime
{
    private PaymentsApp _app = null!;
    private RawBroker _broker = null!;

    public async ValueTask InitializeAsync()
    {
        _app = new PaymentsApp(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString);
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
    public async Task Approved_charge_records_payment_and_publishes_PaymentSucceeded()
    {
        var reservationId = Guid.NewGuid();

        await RequestAsync(reservationId, SimulatedPaymentProvider.ApproveToken);

        await Eventually.AssertAsync(async () => (await OutboxAsync<PaymentSucceeded>()).ShouldHaveSingleItem().Payload.ShouldContain(reservationId.ToString()));
        (await PaymentFor(reservationId)).Status.ShouldBe(PaymentStatus.Succeeded);
    }

    [Fact]
    public async Task Declined_charge_publishes_PaymentFailed()
    {
        var reservationId = Guid.NewGuid();

        await RequestAsync(reservationId, SimulatedPaymentProvider.DeclineToken);

        await Eventually.AssertAsync(async () => (await OutboxAsync<PaymentFailed>()).ShouldHaveSingleItem());
        (await PaymentFor(reservationId)).Status.ShouldBe(PaymentStatus.Declined);
    }

    [Fact]
    public async Task Same_RequestPayment_delivered_twice_charges_once_and_publishes_one_result()
    {
        // RF-1
        var reservationId = Guid.NewGuid();
        var messageId = Guid.CreateVersion7();

        await RequestAsync(reservationId, SimulatedPaymentProvider.ApproveToken, messageId);
        await RequestAsync(reservationId, SimulatedPaymentProvider.ApproveToken, messageId);
        await RequestAsync(Guid.NewGuid(), SimulatedPaymentProvider.ApproveToken); // sentinel

        await Eventually.AssertAsync(async () => (await OutboxAsync<PaymentSucceeded>()).Count.ShouldBe(2));
        (await _app.WithDbAsync(db => db.Payments.CountAsync(p => p.ReservationId == reservationId, Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Provider_outage_is_retried_and_eventually_succeeds()
    {
        var reservationId = Guid.NewGuid();

        await RequestAsync(reservationId, SimulatedPaymentProvider.FlakyToken);

        await Eventually.AssertAsync(async () => (await PaymentFor(reservationId)).Status.ShouldBe(PaymentStatus.Succeeded));
        (await OutboxAsync<PaymentSucceeded>()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Refund_marks_payment_refunded_and_publishes_PaymentRefunded()
    {
        var reservationId = Guid.NewGuid();
        await RequestAsync(reservationId, SimulatedPaymentProvider.ApproveToken);
        await Eventually.AssertAsync(async () => (await PaymentFor(reservationId)).Status.ShouldBe(PaymentStatus.Succeeded));
        var payment = await PaymentFor(reservationId);

        await PublishAsync(new RefundPayment(payment.Id, reservationId, "late"));

        await Eventually.AssertAsync(async () => (await PaymentFor(reservationId)).Status.ShouldBe(PaymentStatus.Refunded));
        (await OutboxAsync<PaymentRefunded>()).ShouldHaveSingleItem();
    }

    private Task RequestAsync(Guid reservationId, string token, Guid? messageId = null) =>
        _broker.PublishMessageAsync(_app.Exchange, MessageTypeAttribute.NameOf<RequestPayment>(),
            new RequestPayment(reservationId, 50m, "EUR", token), messageId);

    private Task PublishAsync<T>(T message) where T : IIntegrationMessage =>
        _broker.PublishMessageAsync(_app.Exchange, MessageTypeAttribute.NameOf(typeof(T)), message);

    private Task<Payment> PaymentFor(Guid reservationId) =>
        _app.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.ReservationId == reservationId, Ct));

    private Task<List<OutboxMessage>> OutboxAsync<T>() where T : IIntegrationMessage
    {
        var type = MessageTypeAttribute.NameOf(typeof(T));
        return _app.WithDbAsync(db => db.Set<OutboxMessage>().AsNoTracking().Where(m => m.Type == type).ToListAsync(Ct));
    }
}
