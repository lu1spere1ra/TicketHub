using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TicketHub.Booking.Data;
using TicketHub.Booking.Domain;

namespace TicketHub.Booking.Features.Admin;

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record SagaSummary(
    Guid ReservationId, Guid EventId, string CustomerEmail, string Status, DateTimeOffset CreatedAt, DateTimeOffset HoldExpiresAt,
    decimal Total, string Currency, Guid? PaymentId, Guid? RefundedPaymentId, DateTimeOffset? RefundedAt, string? LastStep);

public sealed record SagaDetails(SagaSummary Saga, IReadOnlyList<string> Seats, IReadOnlyList<TimelineEntry> Timeline);

public sealed record DeadLetterResponse(
    Guid Id, string? MessageId, string? MessageType, string? OriginalQueue, string? ExceptionType, string? ExceptionMessage,
    int Attempts, DateTimeOffset? FailedAt, string Payload);

/// <summary>Operational view: where every checkout saga stands, and which messages failed for good.</summary>
public static class AdminEndpoints
{
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").WithTags("Admin");

        admin.MapGet("/sagas", async Task<Results<Ok<PagedResponse<SagaSummary>>, BadRequest<string>>> (
            BookingDbContext db, CancellationToken ct, string? status = null, int page = 1, int pageSize = 50) =>
        {
            var query = db.Reservations.AsNoTracking();
            if (status is not null)
            {
                if (!Enum.TryParse<ReservationStatus>(status, ignoreCase: true, out var parsed))
                {
                    return TypedResults.BadRequest($"Unknown status '{status}'.");
                }

                query = query.Where(r => r.Status == parsed);
            }

            (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(r => r.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return TypedResults.Ok(new PagedResponse<SagaSummary>(items.Select(ToSummary).ToList(), page, pageSize, total));
        });

        admin.MapGet("/sagas/{reservationId:guid}", async Task<Results<Ok<SagaDetails>, NotFound>> (
            Guid reservationId, BookingDbContext db, CancellationToken ct) =>
        {
            var reservation = await db.Reservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == reservationId, ct);
            return reservation is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(new SagaDetails(ToSummary(reservation), reservation.Seats.Select(s => s.Label).ToList(), reservation.Timeline));
        });

        admin.MapGet("/dead-letters", async (BookingDbContext db, CancellationToken ct, string? queue = null, int page = 1, int pageSize = 50) =>
        {
            var query = db.DeadLetters.AsNoTracking();
            if (queue is not null)
            {
                query = query.Where(d => d.OriginalQueue == queue);
            }

            (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(d => d.ArchivedAt).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(d => new DeadLetterResponse(d.Id, d.MessageId, d.MessageType, d.OriginalQueue, d.ExceptionType, d.ExceptionMessage, d.Attempts, d.FailedAt, d.Payload))
                .ToListAsync(ct);
            return TypedResults.Ok(new PagedResponse<DeadLetterResponse>(items, page, pageSize, total));
        });

        return app;
    }

    private static SagaSummary ToSummary(Reservation r) => new(
        r.Id, r.EventId, r.CustomerEmail, r.Status.ToString(), r.CreatedAt, r.HoldExpiresAt, r.Total.Amount, r.Total.Currency,
        r.PaymentId, r.RefundedPaymentId, r.RefundedAt, r.Timeline.Count > 0 ? r.Timeline[^1].Step : null);
}
