using TicketHub.Booking;
using TicketHub.Booking.Catalog;
using TicketHub.Booking.Data;
using TicketHub.Booking.Features.Reservations;
using TicketHub.BuildingBlocks;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Catalog.Grpc;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.AddNpgsqlDbContext<BookingDbContext>("bookingdb");
builder.AddTicketHubMessaging<BookingDbContext>();

builder.Services.AddOptions<HoldOptions>().Bind(builder.Configuration.GetSection(HoldOptions.SectionName));
builder.Services.AddGrpcClient<SeatSellability.SeatSellabilityClient>(client =>
    client.Address = new Uri(builder.Configuration["Catalog:GrpcAddress"] ?? "http://_grpc.catalog"));
builder.Services.AddScoped<ICatalogSeatChecker, GrpcCatalogSeatChecker>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HeaderCurrentUser>();
builder.Services.AddScoped<HoldSeatsHandler>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapDefaultEndpoints();
app.MapReservationEndpoints();

await app.MigrateDatabaseAsync<BookingDbContext>();
await app.RunAsync();
