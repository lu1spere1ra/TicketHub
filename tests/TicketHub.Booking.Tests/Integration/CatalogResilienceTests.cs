using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using TicketHub.Catalog.Grpc;
using TicketHub.TestKit;

namespace TicketHub.Booking.Tests.Integration;

/// <summary>
/// The real gRPC client and resilience pipeline against a stub Catalog whose behaviour each test controls (RF-5).
/// </summary>
public sealed class CatalogResilienceTests(PostgresContainer postgres, RabbitMqContainer rabbitMq) : IAsyncLifetime
{
    private enum Mode { Http503, GrpcUnavailable, Healthy }

    private Mode _mode = Mode.Http503;
    private int _hits;
    private WebApplication _stub = null!;
    private BookingApp _app = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Logging.ClearProviders();
        _stub = builder.Build();
        _stub.Run(HandleAsync);
        await _stub.StartAsync();

        _app = new BookingApp(await postgres.CreateDatabaseAsync(), rabbitMq.ConnectionString, realCatalogAddress: _stub.Urls.Single());
        _ = _app.Server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _stub.DisposeAsync();
    }

    [Fact]
    public async Task Grpc_unavailable_status_is_treated_as_transient_and_retried()
    {
        _mode = Mode.GrpcUnavailable;

        var response = await HoldAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        _hits.ShouldBe(3); // first attempt + 2 retries
    }

    [Fact]
    public async Task Hold_returns_503_quickly_once_the_catalog_circuit_is_open()
    {
        for (var i = 0; i < 4; i++)
        {
            await HoldAsync();
        }

        var hitsWhenOpen = _hits;
        var timer = Stopwatch.StartNew();
        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 10; i++)
        {
            responses.Add(await HoldAsync());
        }

        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1)); // 10 calls, no waiting on a dead dependency
        _hits.ShouldBe(hitsWhenOpen);                            // nothing reached Catalog while open
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.ServiceUnavailable);
        responses[0].Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task Holds_reach_catalog_again_after_it_recovers()
    {
        for (var i = 0; i < 4; i++)
        {
            await HoldAsync();
        }

        _mode = Mode.Healthy;
        // Polly reads time from the app's TimeProvider, which these tests control: move past the 1 s break.
        _app.Clock.Advance(TimeSpan.FromSeconds(2));

        // After the break the breaker lets a probe through; the stub answers "event not found", so 404 means Catalog
        // was reached and answered.
        await Eventually.AssertAsync(async () => (await HoldAsync()).StatusCode.ShouldBe(HttpStatusCode.NotFound), TimeSpan.FromSeconds(10));
    }

    private Task<HttpResponseMessage> HoldAsync() =>
        BookingApp.HoldAsync(_app.CreateCustomer("alice"), Guid.NewGuid(), Guid.NewGuid());

    private async Task HandleAsync(HttpContext context)
    {
        Interlocked.Increment(ref _hits);
        switch (_mode)
        {
            case Mode.Http503:
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;

            case Mode.GrpcUnavailable:
                // "Trailers-only" gRPC failure: HTTP 200 with the status in the headers.
                context.Response.ContentType = "application/grpc";
                context.Response.Headers["grpc-status"] = "14";
                return;

            default:
                var message = new CheckSeatsResponse { EventFound = false }.ToByteArray();
                var frame = new byte[5 + message.Length];
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)message.Length);
                message.CopyTo(frame, 5);
                context.Response.ContentType = "application/grpc";
                await context.Response.Body.WriteAsync(frame);
                context.Response.AppendTrailer("grpc-status", "0");
                return;
        }
    }
}
