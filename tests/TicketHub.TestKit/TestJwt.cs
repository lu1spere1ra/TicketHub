using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace TicketHub.TestKit;

/// <summary>
/// Issues Keycloak-shaped tokens signed with a local RSA key, and points a service's JWT validation at that key.
/// Integration tests exercise the real authentication and authorisation pipeline without running Keycloak.
/// </summary>
public static class TestJwt
{
    public const string Issuer = "https://tests.tickethub.local/realms/tickethub";
    public const string Audience = "tickethub-api";

    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test-key" };

    public static string Issue(string userId, string[] roles, string? audience = Audience)
    {
        var claims = new List<Claim>
        {
            new("sub", userId),
            new("email", $"{userId}@example.com"),
            new("preferred_username", userId),
            new("realm_access", JsonSerializer.Serialize(new { roles }), JsonClaimValueTypes.Json),
        };
        var token = new JwtSecurityToken(
            Issuer, audience, claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(15),
            new SigningCredentials(Key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string Customer(string userId) => Issue(userId, ["customer"]);

    public static string Admin(string userId = "ops") => Issue(userId, ["admin"]);

    public static HttpClient WithBearer(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Replaces the Keycloak metadata lookup with this class's issuer and signing key.</summary>
    public static IServiceCollection UseTestJwtValidation(this IServiceCollection services) =>
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Authority = null;
            options.MetadataAddress = null!;
            var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            configuration.SigningKeys.Add(Key);
            options.Configuration = configuration;
            options.TokenValidationParameters.ValidIssuer = Issuer;
            options.TokenValidationParameters.ValidAudience = Audience;
            options.TokenValidationParameters.IssuerSigningKey = Key;
        });
}
