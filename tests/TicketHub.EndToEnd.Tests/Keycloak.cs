using System.Net.Http.Json;
using System.Text.Json;

namespace TicketHub.EndToEnd.Tests;

/// <summary>Talks to the real Keycloak started by the AppHost.</summary>
public sealed class Keycloak(HttpClient http)
{
    private const string Password = "password";

    /// <summary>
    /// Registers a brand-new customer (the realm makes every new user a customer) and returns their access token.
    /// A user per test gives each test its own mailbox in Mailpit.
    /// </summary>
    public async Task<(string Token, string Email)> NewCustomerAsync()
    {
        var username = "e2e-" + Guid.NewGuid().ToString("N")[..10];
        var email = $"{username}@example.com";
        var adminToken = await TokenAsync("master", "admin-cli", "admin", "admin");

        using var create = new HttpRequestMessage(HttpMethod.Post, "/admin/realms/tickethub/users")
        {
            Content = JsonContent.Create(new
            {
                username,
                email,
                firstName = "E2E",
                lastName = "Customer",
                enabled = true,
                emailVerified = true,
                credentials = new[] { new { type = "password", value = Password, temporary = false } },
            }),
        };
        create.Headers.Authorization = new("Bearer", adminToken);
        (await http.SendAsync(create)).EnsureSuccessStatusCode();

        return (await TokenAsync("tickethub", "tickethub-cli", username, Password), email);
    }

    public Task<string> TokenForAsync(string username) => TokenAsync("tickethub", "tickethub-cli", username, Password);

    private async Task<string> TokenAsync(string realm, string clientId, string username, string password)
    {
        var response = await http.PostAsync($"/realms/{realm}/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["username"] = username,
            ["password"] = password,
            ["scope"] = "openid",
        }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }
}
