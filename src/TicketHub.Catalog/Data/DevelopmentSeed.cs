using Microsoft.EntityFrameworkCore;
using TicketHub.BuildingBlocks;
using TicketHub.Catalog.Domain;

namespace TicketHub.Catalog.Data;

/// <summary>Fictional venues and events so the system is usable straight after startup.</summary>
public static class DevelopmentSeed
{
    /// <summary>Stable ids so docs, the .http file and the load test can refer to the seeded events.</summary>
    public static readonly Guid JazzNightId = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    public static readonly Guid LoadTestNightId = Guid.Parse("0197a000-0000-7000-8000-000000000002");

    public static async Task SeedAsync(CatalogDbContext db, IServiceProvider services)
    {
        if (await db.Venues.AnyAsync())
        {
            return;
        }

        var now = services.GetService(typeof(TimeProvider)) is TimeProvider time ? time.GetUtcNow() : DateTimeOffset.UtcNow;

        var hall = Venue.Create("Riverside Hall", Layout(sections: ["A", "B"], rows: 5, seatsPerRow: 10));
        var arena = Venue.Create("Load Test Arena", Layout(sections: ["FLOOR"], rows: 20, seatsPerRow: 50));
        hall.Seats.First(s => s.Section == "A" && s.Row == "1" && s.Number == 1).Block();

        db.Venues.AddRange(hall, arena);
        db.Events.AddRange(
            Event.Schedule(JazzNightId, "Midnight Jazz Session", hall,
                startsAt: now.AddDays(30), salesOpenAt: now.AddDays(-1), salesCloseAt: now.AddDays(29),
                [new SectionPrice("A", 45m, Money.Euro), new SectionPrice("B", 30m, Money.Euro)]),
            Event.Schedule(LoadTestNightId, "Load Test Night", arena,
                startsAt: now.AddDays(60), salesOpenAt: now.AddDays(-1), salesCloseAt: now.AddDays(59),
                [new SectionPrice("FLOOR", 25m, Money.Euro)]));

        await db.SaveChangesAsync();
    }

    private static IEnumerable<(string, string, int)> Layout(string[] sections, int rows, int seatsPerRow) =>
        from section in sections
        from row in Enumerable.Range(1, rows)
        from number in Enumerable.Range(1, seatsPerRow)
        select (section, row.ToString(System.Globalization.CultureInfo.InvariantCulture), number);
}
