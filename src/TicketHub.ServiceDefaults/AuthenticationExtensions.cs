using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// JWT bearer validation shared by the gateway and every service with an API (ADR-0010). Each service validates
/// tokens itself instead of trusting the gateway: a request that reaches a service by another route is still checked.
/// </summary>
public static class AuthenticationExtensions
{
    public const string CustomerPolicy = "customer";
    public const string AdminPolicy = "admin";

    /// <summary>
    /// Configuration (section <c>Auth</c>):
    /// <list type="bullet">
    /// <item><c>Authority</c>: Keycloak realm URL, e.g. http://localhost:5220/realms/tickethub.</item>
    /// <item><c>Audience</c>: defaults to <c>tickethub-api</c>.</item>
    /// <item><c>MetadataAddress</c> / <c>ValidIssuer</c>: optional, for when services reach Keycloak at a different
    /// URL than the one in the token's <c>iss</c> (docker-compose: tokens say localhost, services call keycloak:8080).</item>
    /// </list>
    /// </summary>
    public static TBuilder AddTicketHubAuthentication<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var auth = builder.Configuration.GetSection("Auth");

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = auth["Authority"];
                options.Audience = auth["Audience"] ?? "tickethub-api";
                options.RequireHttpsMetadata = auth.GetValue("RequireHttpsMetadata", false);
                options.MapInboundClaims = false; // keep "sub", "email" as issued
                if (auth["MetadataAddress"] is { Length: > 0 } metadata)
                {
                    options.MetadataAddress = metadata;
                }

                if (auth["ValidIssuer"] is { Length: > 0 } issuer)
                {
                    options.TokenValidationParameters.ValidIssuer = issuer;
                }

                options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                options.TokenValidationParameters.NameClaimType = "preferred_username";
            });

        builder.Services.AddTransient<IClaimsTransformation, KeycloakRolesTransformation>();
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(CustomerPolicy, policy => policy.RequireRole("customer"))
            .AddPolicy(AdminPolicy, policy => policy.RequireRole("admin"));

        return builder;
    }
}

/// <summary>
/// Keycloak puts realm roles in a nested JSON claim, <c>realm_access: { roles: [...] }</c>. ASP.NET Core role checks
/// need one role claim per role, so this flattens them.
/// </summary>
internal sealed class KeycloakRolesTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity
            || identity.HasClaim(c => c.Type == ClaimTypes.Role)
            || principal.FindFirst("realm_access") is not { } realmAccess)
        {
            return Task.FromResult(principal);
        }

        using var json = JsonDocument.Parse(realmAccess.Value);
        if (json.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
        {
            foreach (var role in roles.EnumerateArray())
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role.GetString()!));
            }
        }

        return Task.FromResult(principal);
    }
}
