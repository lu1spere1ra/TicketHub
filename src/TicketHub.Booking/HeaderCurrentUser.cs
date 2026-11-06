using TicketHub.BuildingBlocks;

namespace TicketHub.Booking;

/// <summary>
/// TEMPORARY identity seam for phases 1 to 3: reads the customer from <c>X-User-Id</c> / <c>X-User-Email</c>.
/// Replaced by JWT claims when authentication is added (see docs/PLAN.md, phase 4).
/// </summary>
internal sealed class HeaderCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string UserIdHeader = "X-User-Id";
    public const string EmailHeader = "X-User-Email";

    public string UserId => Header(UserIdHeader);
    public string Email => Header(EmailHeader);

    private string Header(string name) => accessor.HttpContext?.Request.Headers[name].FirstOrDefault() ?? string.Empty;
}

/// <summary>Rejects requests that do not identify a customer (temporary, see <see cref="HeaderCurrentUser"/>).</summary>
internal sealed class RequireCustomerFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var headers = context.HttpContext.Request.Headers;
        return string.IsNullOrEmpty(headers[HeaderCurrentUser.UserIdHeader]) || string.IsNullOrEmpty(headers[HeaderCurrentUser.EmailHeader])
            ? ValueTask.FromResult<object?>(TypedResults.Unauthorized())
            : next(context);
    }
}
