namespace TicketHub.Booking.Catalog;

/// <summary>
/// Calls to Catalog are made on behalf of the customer, so the customer's bearer token is forwarded and Catalog
/// validates it like any other request (ADR-0010).
/// </summary>
internal sealed class ForwardAccessTokenHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (accessor.HttpContext?.Request.Headers.Authorization.ToString() is { Length: > 0 } authorization)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
