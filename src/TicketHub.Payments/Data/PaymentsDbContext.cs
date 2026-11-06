using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Payments.Domain;

namespace TicketHub.Payments.Data;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(payment =>
        {
            payment.HasKey(p => p.Id);
            // One payment per reservation: a second charge for the same reservation is a bug, not a feature.
            payment.HasIndex(p => p.ReservationId).IsUnique();
            payment.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            payment.Property(p => p.ProviderReference).HasMaxLength(100);
            payment.Property(p => p.DeclineReason).HasMaxLength(200);
            payment.ComplexProperty(p => p.Amount, amount => amount.Property(m => m.Currency).HasMaxLength(3));
        });

        modelBuilder.AddMessagingTables();
    }
}

internal sealed class PaymentsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql("Host=localhost;Database=payments").Options);
}
