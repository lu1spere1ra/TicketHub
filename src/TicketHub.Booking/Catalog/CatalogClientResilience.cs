using Grpc.Core;
using Grpc.Net.Client.Configuration;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;

namespace TicketHub.Booking.Catalog;

public sealed class CatalogClientOptions
{
    public const string SectionName = "Catalog";

    public string GrpcAddress { get; set; } = "http://_grpc.catalog";

    /// <summary>Deadline for the whole call, retries included.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(1);
    public int MaxRetries { get; set; } = 2;
    public int CircuitMinimumThroughput { get; set; } = 10;
    public double CircuitFailureRatio { get; set; } = 0.5;
    public TimeSpan CircuitSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Resilience for the one synchronous dependency on the hot path (ADR-0008).
/// <list type="bullet">
/// <item><b>Retries</b> use gRPC's own retry policy. An HTTP-level retry cannot work for gRPC: the request body is a
/// single-use stream, so a re-sent attempt hangs. gRPC buffers the message and replays it correctly.
/// <c>CheckSeats</c> is a read, so retrying it is safe.</item>
/// <item><b>Circuit breaker and per-attempt timeout</b> come from Microsoft.Extensions.Http.Resilience and wrap every
/// HTTP attempt, so they see each retry individually.</item>
/// <item><b>Total timeout</b> is the gRPC call deadline, set per call by <see cref="GrpcCatalogSeatChecker"/>.</item>
/// </list>
/// </summary>
public static class CatalogClientResilience
{
    private const string GrpcStatusHeader = "grpc-status";
    private const string Unavailable = "14";
    private const string DeadlineExceeded = "4";

    public static IHttpClientBuilder AddCatalogResilience(this IHttpClientBuilder builder, CatalogClientOptions options)
    {
        // An HTTP/2 connection carries at most 100 concurrent streams (Kestrel's default). Without extra connections,
        // call 101 onwards queues inside the client during a sale burst and burns its timeout before it is even sent.
        builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        });

        builder.ConfigureChannel(channel => channel.ServiceConfig = new ServiceConfig
        {
            MethodConfigs =
            {
                new MethodConfig
                {
                    Names = { MethodName.Default },
                    RetryPolicy = new RetryPolicy
                    {
                        MaxAttempts = options.MaxRetries + 1,
                        InitialBackoff = TimeSpan.FromMilliseconds(100),
                        MaxBackoff = TimeSpan.FromSeconds(1),
                        BackoffMultiplier = 2,
                        RetryableStatusCodes = { StatusCode.Unavailable },
                    },
                },
            },
        });

        builder.AddResilienceHandler("catalog", pipeline => pipeline
            .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),
                MinimumThroughput = options.CircuitMinimumThroughput,
                FailureRatio = options.CircuitFailureRatio,
                SamplingDuration = options.CircuitSamplingDuration,
                BreakDuration = options.CircuitBreakDuration,
            })
            .AddTimeout(options.AttemptTimeout));

        return builder;
    }

    /// <summary>
    /// HTTP-level handlers never see a gRPC status that arrives in trailers: a failing gRPC call can be an HTTP 200.
    /// When the server fails before sending a body it answers "trailers-only", which puts <c>grpc-status</c> in the
    /// response headers. Those are visible here, so Unavailable and DeadlineExceeded count as failures too.
    /// </summary>
    public static bool IsTransient(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is HttpRequestException or TimeoutRejectedException)
        {
            return true;
        }

        if (HttpClientResiliencePredicates.IsTransient(outcome))
        {
            return true;
        }

        return outcome.Result is { } response
            && response.Headers.TryGetValues(GrpcStatusHeader, out var values)
            && values.FirstOrDefault() is Unavailable or DeadlineExceeded;
    }
}
