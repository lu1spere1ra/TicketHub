using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using TicketHub.TestKit;

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
            builder.ConfigureTestServices(services => services.UseTestJwtValidation());
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
    [InlineData("/admin/sagas", "booking:/admin/sagas")]
    public async Task Routes_prefixes_to_their_service(string path, string expected)
    {
        var client = _gateway.CreateClient().WithBearer(TestJwt.Issue("someone", ["customer", "admin"]));

        var response = await client.GetStringAsync(path, TestContext.Current.CancellationToken);

        response.ShouldStartWith(expected);
    }

    [Fact]
    public async Task Catalog_is_public()
    {
        var response = await _gateway.CreateClient().GetAsync("/catalog/events", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Gateway_rejects_missing_token_before_reaching_booking()
    {
        var response = await _gateway.CreateClient().GetAsync("/booking/reservations", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _bookingHits.ShouldBe(0);
    }

    [Fact]
    public async Task Gateway_rejects_customers_on_admin_routes()
    {
        var client = _gateway.CreateClient().WithBearer(TestJwt.Customer("alice"));

        var response = await client.GetAsync("/admin/sagas", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Gateway_forwards_the_bearer_token_so_services_can_validate_it_again()
    {
        var client = _gateway.CreateClient().WithBearer(TestJwt.Customer("alice"));

        var response = await client.GetStringAsync("/booking/reservations", TestContext.Current.CancellationToken);

        response.ShouldEndWith("|auth=Bearer");
    }

    [Fact]
    public async Task Unknown_route_returns_404()
    {
        var response = await _gateway.CreateClient().GetAsync("/payments/anything", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private int _bookingHits;

    private async Task<WebApplication> StartStubAsync(string name)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Run(context =>
        {
            if (name == "booking")
            {
                Interlocked.Increment(ref _bookingHits);
            }

            var auth = context.Request.Headers.Authorization.ToString().Split(' ')[0];
            return context.Response.WriteAsync($"{name}:{context.Request.Path}|auth={auth}");
        });
        await app.StartAsync();
        return app;
    }
}
