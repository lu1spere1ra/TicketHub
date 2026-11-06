var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddTicketHubAuthentication();

// Routes and clusters live in configuration; destinations such as http://catalog are resolved through service
// discovery (Aspire in development, environment variables in compose). The proxy never retries: replaying a
// non-idempotent POST at the edge could hold seats twice (see docs/adr/0008-resilience-policies.md).
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

var app = builder.Build();

// The edge rejects unauthenticated or unauthorised calls before they cost a backend anything; services still
// validate the token themselves (defence in depth). Each route's policy is set in appsettings.json.
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapReverseProxy();

await app.RunAsync();
