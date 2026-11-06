using TicketHub.BuildingBlocks;

namespace TicketHub.Booking;

/// <summary>The authenticated customer, read from the validated JWT (<c>sub</c> and <c>email</c> claims).</summary>
internal sealed class ClaimsCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string UserId => Claim("sub");
    public string Email => Claim("email");

    // Endpoints using ICurrentUser all require an authenticated customer, so a missing claim is a configuration bug.
    private string Claim(string type) =>
        accessor.HttpContext?.User.FindFirst(type)?.Value
        ?? throw new InvalidOperationException($"The access token has no '{type}' claim.");
}
