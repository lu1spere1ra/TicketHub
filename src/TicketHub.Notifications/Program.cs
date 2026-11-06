using TicketHub.BuildingBlocks;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Contracts.Booking;
using TicketHub.Notifications.Data;
using TicketHub.Notifications.Emails;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.AddNpgsqlDbContext<NotificationsDbContext>("notificationsdb");
builder.AddTicketHubMessaging<NotificationsDbContext>(messaging => messaging
    .Consumes<ReservationConfirmed, ReservationConfirmedHandler>("notifications.booking-events")
    .Consumes<ReservationCancelled, ReservationCancelledHandler>("notifications.booking-events")
    .Consumes<ReservationExpired, ReservationExpiredHandler>("notifications.booking-events"));

builder.Services.AddOptions<SmtpOptions>().Bind(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

var app = builder.Build();

app.MapDefaultEndpoints();

await app.MigrateDatabaseAsync<NotificationsDbContext>();
await app.RunAsync();
