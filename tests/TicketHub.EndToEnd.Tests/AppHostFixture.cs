using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: AssemblyFixture(typeof(TicketHub.EndToEnd.Tests.AppHostFixture))]

namespace TicketHub.EndToEnd.Tests;

/// <summary>
/// Starts the whole system (the real AppHost: containers and services) once for the test assembly.
/// Holds expire after a few seconds here so the expiry path can be observed in a test.
/// </summary>
public sealed class AppHostFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(4);
    private DistributedApplication? _app;

    public DistributedApplication App => _app ?? throw new InvalidOperationException("AppHost not started.");

    public async ValueTask InitializeAsync()
    {
        using var cts = new CancellationTokenSource(StartupTimeout);
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.TicketHub_AppHost>(
            [
                "--Booking:Holds:Duration=00:00:05",
                "--Booking:Expiry:PollInterval=00:00:01",
                // Outcomes are chosen with deterministic test card tokens instead of dice.
                "--Payments:Simulator:DeclineRate=0",
                "--Payments:Simulator:TransientFailureRate=0",
            ],
            cts.Token);
        builder.Services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        _app = await builder.BuildAsync(cts.Token);
        await _app.StartAsync(cts.Token);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("gateway", cts.Token);
    }

    public HttpClient CreateGatewayClient() => App.CreateHttpClient("gateway");

    public HttpClient CreateMailpitClient() => App.CreateHttpClient("mailpit", "http");

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
