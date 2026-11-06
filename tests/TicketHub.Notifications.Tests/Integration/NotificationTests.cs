using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Contracts.Booking;
using TicketHub.TestKit;

namespace TicketHub.Notifications.Tests.Integration;

public sealed class NotificationTests(PostgresContainer postgres, RabbitMqContainer rabbitMq, MailpitContainer mailpit) : IAsyncLifetime
{
    private readonly string _exchange = "t" + Guid.NewGuid().ToString("N")[..12] + ".events";
    private WebApplicationFactory<Program> _app = null!;
    private RawBroker _broker = null!;
    private readonly HttpClient _mailpit = new() { BaseAddress = mailpit.ApiBaseAddress };

    public async ValueTask InitializeAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:notificationsdb", database);
            builder.UseSetting("ConnectionStrings:rabbitmq", rabbitMq.ConnectionString);
            builder.UseSetting("Messaging:Exchange", _exchange);
            builder.UseSetting("Messaging:DeadLetterQueue", _exchange + ".dead-letters");
            builder.UseSetting("Messaging:QueuePrefix", _exchange + ".");
            builder.UseSetting("Smtp:Host", mailpit.SmtpHost);
            builder.UseSetting("Smtp:Port", mailpit.SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.ConfigureLogging(logging => logging.ClearProviders());
        });
        _ = _app.Server;
        _broker = await RawBroker.ConnectAsync(rabbitMq.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        await _app.DisposeAsync();
        _mailpit.Dispose();
    }

    [Fact]
    public async Task Confirmed_reservation_sends_one_email_even_when_delivered_twice()
    {
        // RF-1
        var to = $"{Guid.NewGuid():N}@example.com";
        var messageId = Guid.CreateVersion7();
        var confirmed = new ReservationConfirmed(Guid.NewGuid(), Guid.NewGuid(), to, ["B-11"], 30m, "EUR", DateTimeOffset.UtcNow);
        var type = MessageTypeAttribute.NameOf<ReservationConfirmed>();

        await _broker.PublishMessageAsync(_exchange, type, confirmed, messageId);
        await _broker.PublishMessageAsync(_exchange, type, confirmed, messageId);
        // Sentinel to the same inbox: once it has arrived, both copies above were handled.
        var sentinel = $"{Guid.NewGuid():N}@example.com";
        await _broker.PublishMessageAsync(_exchange, MessageTypeAttribute.NameOf<ReservationExpired>(),
            new ReservationExpired(Guid.NewGuid(), Guid.NewGuid(), sentinel, DateTimeOffset.UtcNow));

        await Eventually.AssertAsync(async () => (await CountEmailsToAsync(sentinel)).ShouldBe(1));
        (await CountEmailsToAsync(to)).ShouldBe(1);
    }

    private async Task<int> CountEmailsToAsync(string address)
    {
        var result = await _mailpit.GetFromJsonAsync<JsonElement>($"/api/v1/search?query=to:{address}", TestContext.Current.CancellationToken);
        return result.GetProperty("messages_count").GetInt32();
    }
}
