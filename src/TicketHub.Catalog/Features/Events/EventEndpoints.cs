using Microsoft.AspNetCore.Http.HttpResults;

namespace TicketHub.Catalog.Features.Events;

public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/events").WithTags("Events");

        events.MapGet("/", (EventQueries queries, CancellationToken ct, int page = 1, int pageSize = 20) =>
                queries.ListAsync(page, pageSize, ct))
            .WithName("ListEvents");

        events.MapGet("/{eventId:guid}", async Task<Results<Ok<EventDetails>, NotFound>> (Guid eventId, EventQueries queries, CancellationToken ct) =>
                await queries.GetAsync(eventId, ct) is { } details ? TypedResults.Ok(details) : TypedResults.NotFound())
            .WithName("GetEvent");

        events.MapGet("/{eventId:guid}/seat-map", async Task<Results<Ok<SeatMapResponse>, NotFound>> (Guid eventId, EventQueries queries, CancellationToken ct) =>
                await queries.GetSeatMapAsync(eventId, ct) is { } map ? TypedResults.Ok(map) : TypedResults.NotFound())
            .WithName("GetSeatMap");

        return app;
    }
}
