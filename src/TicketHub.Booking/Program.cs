using TicketHub.Booking;
using TicketHub.Booking.Catalog;
using TicketHub.Booking.Data;
using TicketHub.Booking.Features.Admin;
using TicketHub.Booking.Features.Checkout;
using TicketHub.Booking.Features.Reservations;
using TicketHub.BuildingBlocks;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Catalog.Grpc;
using TicketHub.Contracts.Payments;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddTicketHubAuthentication();
builder.Services.AddSingleton(TimeProvider.System);
builder.AddNpgsqlDbContext<BookingDbContext>("bookingdb");
builder.AddTicketHubMessaging<BookingDbContext>(messaging => messaging
    .Consumes<PaymentSucceeded, PaymentSucceededHandler>("booking.payment-events")
    .Consumes<PaymentFailed, PaymentFailedHandler>("booking.payment-events")
    .Consumes<PaymentRefunded, PaymentRefundedHandler>("booking.payment-events"));

builder.Services.AddOptions<HoldOptions>().Bind(builder.Configuration.GetSection(HoldOptions.SectionName));
builder.Services.AddOptions<CatalogClientOptions>().Bind(builder.Configuration.GetSection(CatalogClientOptions.SectionName));
var catalogOptions = builder.Configuration.GetSection(CatalogClientOptions.SectionName).Get<CatalogClientOptions>() ?? new();
builder.Services.AddTransient<ForwardAccessTokenHandler>();
builder.Services.AddGrpcClient<SeatSellability.SeatSellabilityClient>(client => client.Address = new Uri(catalogOptions.GrpcAddress))
    .AddHttpMessageHandler<ForwardAccessTokenHandler>()
    .AddCatalogResilience(catalogOptions);
builder.Services.AddScoped<ICatalogSeatChecker, GrpcCatalogSeatChecker>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
builder.Services.AddScoped<HoldSeatsHandler>();
builder.Services.AddScoped<CheckoutHandler>();
builder.Services.AddOptions<HoldExpiryOptions>().Bind(builder.Configuration.GetSection(HoldExpiryOptions.SectionName));
builder.Services.AddSingleton<HoldExpiryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HoldExpiryService>());
builder.Services.AddHostedService<DeadLetterArchiver>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapDefaultEndpoints();
app.MapReservationEndpoints();
app.MapCheckoutEndpoints();
app.MapAdminEndpoints();

await app.MigrateDatabaseAsync<BookingDbContext>();
await app.RunAsync();
