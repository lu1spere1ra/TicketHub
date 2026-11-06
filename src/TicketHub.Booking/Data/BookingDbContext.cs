using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TicketHub.Booking.Domain;
using TicketHub.Booking.Features.Admin;
using TicketHub.BuildingBlocks.Messaging;

namespace TicketHub.Booking.Data;

public sealed class BookingDbContext(DbContextOptions<BookingDbContext> options) : DbContext(options)
{
    public const string SeatAllocationsTable = "SeatAllocations";

    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<SeatAllocation> SeatAllocations => Set<SeatAllocation>();
    public DbSet<DeadLetter> DeadLetters => Set<DeadLetter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SeatAllocation>(allocation =>
        {
            allocation.ToTable(SeatAllocationsTable);
            allocation.HasKey(a => new { a.EventId, a.SeatId });
            allocation.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            allocation.Property(a => a.Version).IsConcurrencyToken();
            allocation.HasIndex(a => a.ReservationId);
        });

        modelBuilder.Entity<Reservation>(reservation =>
        {
            reservation.HasKey(r => r.Id);
            reservation.Property(r => r.CustomerId).HasMaxLength(100);
            reservation.Property(r => r.CustomerEmail).HasMaxLength(320);
            reservation.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            reservation.Property(r => r.Version).IsConcurrencyToken();
            reservation.ComplexProperty(r => r.Total, total => total.Property(m => m.Currency).HasMaxLength(3));
            reservation.Ignore(r => r.Seats);
            reservation.ComplexCollection<List<ReservedSeat>, ReservedSeat>("_seats", seats =>
            {
                seats.ToJson("Seats");
                seats.ComplexProperty(s => s.Price);
            });
            reservation.ComplexCollection<List<TimelineEntry>, TimelineEntry>("_timeline", timeline => timeline.ToJson("Timeline"));
            reservation.Ignore(r => r.Timeline);
            reservation.Property(r => r.CancellationReason).HasMaxLength(200);
            reservation.HasIndex(r => new { r.Status, r.HoldExpiresAt });
            reservation.HasIndex(r => r.CustomerId);
        });

        modelBuilder.Entity<DeadLetter>(deadLetter =>
        {
            deadLetter.HasKey(d => d.Id);
            deadLetter.Property(d => d.MessageType).HasMaxLength(200);
            deadLetter.Property(d => d.OriginalQueue).HasMaxLength(200);
            deadLetter.Property(d => d.ExceptionType).HasMaxLength(500);
            deadLetter.HasIndex(d => d.ArchivedAt);
        });

        modelBuilder.AddMessagingTables();
    }
}

internal sealed class BookingDbContextDesignTimeFactory : IDesignTimeDbContextFactory<BookingDbContext>
{
    public BookingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BookingDbContext>().UseNpgsql("Host=localhost;Database=booking").Options);
}
