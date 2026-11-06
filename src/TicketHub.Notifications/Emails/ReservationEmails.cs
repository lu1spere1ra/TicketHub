using System.Globalization;
using System.Net;
using TicketHub.Contracts.Booking;

namespace TicketHub.Notifications.Emails;

public sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody);

/// <summary>Turns reservation events into customer emails. Pure functions, so they are unit-tested directly.</summary>
public static class ReservationEmails
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static EmailMessage Confirmed(ReservationConfirmed e)
    {
        var seats = string.Join(", ", e.SeatLabels);
        var total = e.TotalAmount.ToString("0.00", Culture) + " " + e.Currency;
        return new EmailMessage(
            e.CustomerEmail,
            "Your tickets are confirmed",
            $"Your reservation {e.ReservationId} is confirmed.\nSeats: {seats}\nTotal paid: {total}\n",
            $"<p>Your reservation <strong>{e.ReservationId}</strong> is confirmed.</p><p>Seats: {Html(seats)}<br>Total paid: {Html(total)}</p>");
    }

    public static EmailMessage Cancelled(ReservationCancelled e) => new(
        e.CustomerEmail,
        "Your payment did not go through",
        $"We could not complete reservation {e.ReservationId}: {e.Reason}.\nThe seats were released; you can try again.\n",
        $"<p>We could not complete reservation <strong>{e.ReservationId}</strong>: {Html(e.Reason)}.</p><p>The seats were released; you can try again.</p>");

    public static EmailMessage Expired(ReservationExpired e) => new(
        e.CustomerEmail,
        "Your seat reservation expired",
        $"Reservation {e.ReservationId} was not paid in time and its seats were released.\n",
        $"<p>Reservation <strong>{e.ReservationId}</strong> was not paid in time and its seats were released.</p>");

    private static string Html(string value) => WebUtility.HtmlEncode(value);
}
