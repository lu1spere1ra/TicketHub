using TicketHub.BuildingBlocks;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Catalog.Data;
using TicketHub.Catalog.Features.Events;
using TicketHub.Catalog.Features.SeatAvailability;
using TicketHub.Catalog.Grpc;
using TicketHub.Contracts.Booking;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddTicketHubAuthentication();
builder.Services.AddSingleton(TimeProvider.System);
builder.AddNpgsqlDbContext<CatalogDbContext>("catalogdb");
builder.AddTicketHubMessaging<CatalogDbContext>(messaging => messaging
    .Consumes<SeatAvailabilityChanged, SeatAvailabilityChangedHandler>("catalog.booking-events"));

builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();
builder.Services.AddScoped<EventQueries>();
builder.Services.AddGrpc();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapDefaultEndpoints();
app.MapEventEndpoints();
// Browsing the catalog is public; the gRPC API is for Booking, which calls it with the customer's token.
app.MapGrpcService<SeatSellabilityService>().RequireAuthorization();

await app.MigrateDatabaseAsync<CatalogDbContext>(DevelopmentSeed.SeedAsync);
await app.RunAsync();
