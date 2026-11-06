using TicketHub.BuildingBlocks;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Catalog.Data;
using TicketHub.Catalog.Features.Events;
using TicketHub.Catalog.Features.SeatAvailability;
using TicketHub.Catalog.Grpc;
using TicketHub.Contracts.Booking;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.AddNpgsqlDbContext<CatalogDbContext>("catalogdb");
builder.AddTicketHubMessaging<CatalogDbContext>(messaging => messaging
    .Consumes<SeatAvailabilityChanged, SeatAvailabilityChangedHandler>("catalog.booking-events"));

builder.Services.AddScoped<EventQueries>();
builder.Services.AddGrpc();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapDefaultEndpoints();
app.MapEventEndpoints();
app.MapGrpcService<SeatSellabilityService>();

await app.MigrateDatabaseAsync<CatalogDbContext>(DevelopmentSeed.SeedAsync);
await app.RunAsync();
