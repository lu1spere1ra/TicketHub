using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace TicketHub.Gateway.Tests;

/// <summary>Runs the real gateway against two stub backends that echo which service received what path.</summary>
public sealed class RoutingTests : IAsyncLifetime
{
    private WebApplication _catalog = null!;
    private WebApplication _booking = null!;
    private WebApplicationFactory<Program> _gateway = null!;

    public async ValueTask InitializeAsync()
    {
        _catalog = await StartStubAsync("catalog");
        _booking = await StartStubAsync("booking");
        _gateway = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ReverseProxy:Clusters:catalog:Destinations:catalog:Address", _catalog.Urls.Single());
            builder.UseSetting("ReverseProxy:Clusters:booking:Destinations:booking:Address", _booking.Urls.Single());
            builder.ConfigureLogging(logging => logging.ClearProviders());
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _gateway.DisposeAsync();
        await _catalog.DisposeAsync();
        await _booking.DisposeAsync();
    }

    [Theory]
    [InlineData("/catalog/events", "catalog:/events")]
    [InlineData("/catalog/events/42/seat-map", "catalog:/events/42/seat-map")]
    [InlineData("/booking/reservations", "booking:/reservations")]
    public async Task Routes_prefixes_to_their_service_and_strips_the_prefix(string path, string expected)
    {
        var response = await _gateway.CreateClient().GetStringAsync(path, TestContext.Current.CancellationToken);

        response.ShouldBe(expected);
    }

    [Fact]
    public async Task Unknown_route_returns_404()
    {
        var response = await _gateway.CreateClient().GetAsync("/payments/anything", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<WebApplication> StartStubAsync(string name)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Run(context => context.Response.WriteAsync($"{name}:{context.Request.Path}"));
        await app.StartAsync();
        return app;
    }
}
