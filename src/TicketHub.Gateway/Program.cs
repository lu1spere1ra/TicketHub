var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Routes and clusters live in configuration; destinations such as http://catalog are resolved through service
// discovery (Aspire in development, environment variables in compose). The proxy never retries: replaying a
// non-idempotent POST at the edge could hold seats twice (see docs/adr/0008-resilience-policies.md).
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapReverseProxy();

await app.RunAsync();
