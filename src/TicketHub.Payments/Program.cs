using TicketHub.BuildingBlocks;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Contracts.Payments;
using TicketHub.Payments.Data;
using TicketHub.Payments.Features;
using TicketHub.Payments.Provider;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.AddNpgsqlDbContext<PaymentsDbContext>("paymentsdb");
builder.AddTicketHubMessaging<PaymentsDbContext>(messaging => messaging
    .Consumes<RequestPayment, RequestPaymentHandler>("payments.commands")
    .Consumes<RefundPayment, RefundPaymentHandler>("payments.commands"));

builder.Services.AddOptions<PaymentSimulatorOptions>().Bind(builder.Configuration.GetSection(PaymentSimulatorOptions.SectionName));
builder.Services.AddSingleton<IPaymentProvider, SimulatedPaymentProvider>();

var app = builder.Build();

// Payments has no public API: it only reacts to messages. HTTP serves health probes.
app.MapDefaultEndpoints();

await app.MigrateDatabaseAsync<PaymentsDbContext>();
await app.RunAsync();
