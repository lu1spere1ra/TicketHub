using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketHub.Booking.Data;
using TicketHub.Booking.Domain;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Contracts.Booking;

namespace TicketHub.Booking.Features.Checkout;

public sealed class HoldExpiryOptions
{
    public const string SectionName = "Booking:Expiry";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int BatchSize { get; set; } = 100;
}

/// <summary>
/// Saga timeout: releases the seats of reservations whose deadline passed without a confirmed payment.
/// Expired reservations are claimed with <c>FOR UPDATE SKIP LOCKED</c>, so any number of Booking replicas can run
/// this service and each reservation is expired exactly once.
/// </summary>
public sealed partial class HoldExpiryService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<HoldExpiryOptions> options,
    ILogger<HoldExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.PollInterval, timeProvider);
        do
        {
            try
            {
                int expired;
                do
                {
                    expired = await ExpireBatchAsync(stoppingToken);
                    if (expired > 0)
                    {
                        LogExpired(expired);
                    }
                }
                while (expired == options.Value.BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
            }
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    public async Task<int> ExpireBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            var now = timeProvider.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(token);

            var held = nameof(ReservationStatus.Held);
            var awaitingPayment = nameof(ReservationStatus.AwaitingPayment);
            var reservations = await db.Reservations
                .FromSql($"""
                    SELECT * FROM "Reservations"
                    WHERE "Status" IN ({held}, {awaitingPayment}) AND "HoldExpiresAt" <= {now}
                    ORDER BY "HoldExpiresAt"
                    LIMIT {options.Value.BatchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(token);

            foreach (var reservation in reservations)
            {
                if (reservation.Expire(now).IsSuccess)
                {
                    await ReservationSeats.ReleaseAsync(db, outbox, reservation, token);
                    outbox.Enqueue(new ReservationExpired(reservation.Id, reservation.EventId, reservation.CustomerEmail, now), reservation.Id);
                }
            }

            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return reservations.Count;
        }, ct);
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} reservation(s) and released their seats")]
    private partial void LogExpired(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hold expiry pass failed; retrying on the next tick")]
    private partial void LogFailed(Exception exception);
}
