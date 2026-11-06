using System.Net.Http.Json;

namespace TicketHub.EndToEnd.Tests;

/// <summary>Thin client over the gateway, using only the public HTTP API.</summary>
public sealed class Api(HttpClient http)
{
    public static readonly Guid JazzNight = Guid.Parse("0197a000-0000-7000-8000-000000000001");

    public static Api WithToken(HttpClient http, string accessToken)
    {
        http.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
        return new Api(http);
    }

    public HttpClient Http => http;

    public async Task<IReadOnlyList<Seat>> SeatsAsync(Guid eventId)
    {
        var map = await http.GetFromJsonAsync<SeatMap>($"/catalog/events/{eventId}/seat-map");
        return map!.Sections.SelectMany(s => s.Rows).SelectMany(r => r.Seats).ToList();
    }

    public async Task<string> SeatStatusAsync(Guid eventId, Guid seatId) =>
        (await SeatsAsync(eventId)).Single(s => s.SeatId == seatId).Status;

    /// <summary>Picks seats nobody else in the test run is likely to touch.</summary>
    public async Task<Guid[]> FreeSeatsAsync(Guid eventId, int count) =>
        (await SeatsAsync(eventId)).Where(s => s.Status == "Available").OrderBy(_ => Random.Shared.Next()).Take(count).Select(s => s.SeatId).ToArray();

    public Task<HttpResponseMessage> HoldAsync(Guid eventId, params Guid[] seatIds) =>
        http.PostAsJsonAsync("/booking/reservations", new { eventId, seatIds });

    public Task<HttpResponseMessage> CheckoutAsync(Guid reservationId, string paymentToken) =>
        http.PostAsJsonAsync($"/booking/reservations/{reservationId}/checkout", new { paymentToken });

    public async Task<Reservation> GetReservationAsync(Guid reservationId) =>
        (await http.GetFromJsonAsync<Reservation>($"/booking/reservations/{reservationId}"))!;

    public sealed record SeatMap(IReadOnlyList<Section> Sections);
    public sealed record Section(string Name, IReadOnlyList<SeatRow> Rows);
    public sealed record SeatRow(string Row, IReadOnlyList<Seat> Seats);
    public sealed record Seat(Guid SeatId, string Label, string Status);
    public sealed record Reservation(Guid Id, string Status, decimal Total);
}
