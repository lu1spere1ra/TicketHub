using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TicketHub.BuildingBlocks;

public static class DatabaseStartup
{
    /// <summary>
    /// Applies pending EF Core migrations, then runs an optional seed, before the host starts its background
    /// services. Controlled by <c>Database:MigrateOnStartup</c> (default true), which suits local runs and the
    /// compose stack. A production deployment would run a migration bundle as a separate step instead.
    /// EF Core takes a database lock while migrating, so concurrent replicas cannot apply the same migration twice.
    /// </summary>
    public static async Task MigrateDatabaseAsync<TDbContext>(this IHost host, Func<TDbContext, IServiceProvider, Task>? seed = null)
        where TDbContext : DbContext
    {
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        if (!configuration.GetValue("Database:MigrateOnStartup", true))
        {
            return;
        }

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await db.Database.MigrateAsync();
        if (seed is not null)
        {
            await seed(db, scope.ServiceProvider);
        }
    }
}
