using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TicketHub.Catalog.Data;
using TicketHub.Catalog.Grpc;

namespace TicketHub.Catalog.Tests.Integration;

/// <summary>The real Catalog host against a fresh PostgreSQL database and an isolated RabbitMQ exchange.</summary>
public sealed class CatalogApp(string database, string rabbitMq) : WebApplicationFactory<Program>
{
    public string Exchange { get; } = "t" + Guid.NewGuid().ToString("N")[..12] + ".events";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:catalogdb", database);
        builder.UseSetting("ConnectionStrings:rabbitmq", rabbitMq);
        builder.UseSetting("Messaging:Exchange", Exchange);
        builder.UseSetting("Messaging:DeadLetterQueue", Exchange + ".dead-letters");
        builder.UseSetting("Messaging:RetryDelay", "00:00:00.200");
        builder.ConfigureLogging(logging => logging.ClearProviders());
    }

    public SeatSellability.SeatSellabilityClient CreateGrpcClient() =>
        new(GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Server.CreateHandler() }));

    public async Task<T> WithDbAsync<T>(Func<CatalogDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<CatalogDbContext>());
    }
}
