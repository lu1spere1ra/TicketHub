using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TicketHub.Catalog.Data;
using TicketHub.Catalog.Grpc;
using TicketHub.TestKit;

namespace TicketHub.Catalog.Tests.Integration;

/// <summary>The real Catalog host against a fresh PostgreSQL database and an isolated RabbitMQ exchange.</summary>
public sealed class CatalogApp(string database, string rabbitMq, string redis) : WebApplicationFactory<Program>
{
    public string Exchange { get; } = "t" + Guid.NewGuid().ToString("N")[..12] + ".events";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:catalogdb", database);
        builder.UseSetting("ConnectionStrings:rabbitmq", rabbitMq);
        builder.UseSetting("ConnectionStrings:redis", redis);
        builder.UseSetting("Messaging:Exchange", Exchange);
        builder.UseSetting("Messaging:DeadLetterQueue", Exchange + ".dead-letters");
        builder.UseSetting("Messaging:QueuePrefix", Exchange + ".");
        builder.UseSetting("Messaging:RetryDelay", "00:00:00.200");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        // Test apps share one Redis but each has its own database (and seat ids): keep their cache keys apart.
        builder.ConfigureTestServices(services =>
        {
            services.Configure<RedisCacheOptions>(options => options.InstanceName = Exchange + ":");
            services.UseTestJwtValidation();
        });
    }

    /// <summary>gRPC client calling as a customer (Booking forwards the customer's token), or anonymously.</summary>
    public SeatSellability.SeatSellabilityClient CreateGrpcClient(bool authenticated = true)
    {
        HttpMessageHandler handler = Server.CreateHandler();
        if (authenticated)
        {
            handler = new BearerHandler(TestJwt.Customer("booking-caller")) { InnerHandler = handler };
        }

        return new(GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler }));
    }

    private sealed class BearerHandler(string token) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, cancellationToken);
        }
    }

    public async Task<T> WithDbAsync<T>(Func<CatalogDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<CatalogDbContext>());
    }
}
