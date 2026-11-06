using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Catalog.Domain;

namespace TicketHub.Catalog.Data;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<EventSeatStatus> SeatStatuses => Set<EventSeatStatus>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Venue>(venue =>
        {
            venue.HasKey(v => v.Id);
            venue.Property(v => v.Name).HasMaxLength(200);
            venue.HasMany(v => v.Seats).WithOne().HasForeignKey(s => s.VenueId);
            venue.Navigation(v => v.Seats).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Seat>(seat =>
        {
            seat.HasKey(s => s.Id);
            seat.Property(s => s.Section).HasMaxLength(20);
            seat.Property(s => s.Row).HasMaxLength(10);
            seat.Ignore(s => s.Label);
            seat.HasIndex(s => new { s.VenueId, s.Section, s.Row, s.Number }).IsUnique();
        });

        modelBuilder.Entity<Event>(@event =>
        {
            @event.HasKey(e => e.Id);
            @event.Property(e => e.Name).HasMaxLength(200);
            @event.HasOne<Venue>().WithMany().HasForeignKey(e => e.VenueId);
            @event.HasIndex(e => e.StartsAt);
            @event.OwnsMany(e => e.Prices, prices =>
            {
                prices.ToJson();
                prices.Property(p => p.Section).HasMaxLength(20);
                prices.Property(p => p.Currency).HasMaxLength(3);
            });
            @event.Navigation(e => e.Prices).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<EventSeatStatus>(status =>
        {
            status.HasKey(s => new { s.EventId, s.SeatId });
            status.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            // Two replicas projecting changes for the same seat must not overwrite a newer version with an older
            // one: the version doubles as the optimistic concurrency token.
            status.Property(s => s.Version).IsConcurrencyToken();
        });

        modelBuilder.AddMessagingTables();
    }
}

/// <summary>Lets <c>dotnet ef</c> build the model without starting the Aspire-configured host.</summary>
internal sealed class CatalogDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql("Host=localhost;Database=catalog").Options);
}
