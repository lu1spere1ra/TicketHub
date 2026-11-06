using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TicketHub.Payments.Data;

namespace TicketHub.Payments.Tests.Integration;

public sealed class PaymentsApp(string database, string rabbitMq) : WebApplicationFactory<Program>
{
    public string Exchange { get; } = "t" + Guid.NewGuid().ToString("N")[..12] + ".events";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:paymentsdb", database);
        builder.UseSetting("ConnectionStrings:rabbitmq", rabbitMq);
        builder.UseSetting("Messaging:Exchange", Exchange);
        builder.UseSetting("Messaging:DeadLetterQueue", Exchange + ".dead-letters");
        builder.UseSetting("Messaging:QueuePrefix", Exchange + ".");
        builder.UseSetting("Messaging:RetryDelay", "00:00:00.200");
        builder.UseSetting("Payments:Simulator:DeclineRate", "0");
        builder.UseSetting("Payments:Simulator:TransientFailureRate", "0");
        builder.UseSetting("Payments:Simulator:MaxLatency", "00:00:00.010");
        builder.ConfigureLogging(logging => logging.ClearProviders());
    }

    public async Task<T> WithDbAsync<T>(Func<PaymentsDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
    }
}
