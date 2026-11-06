using TicketHub.Contracts.Booking;
using TicketHub.Notifications.Emails;

namespace TicketHub.Notifications.Tests.Domain;

public class ReservationEmailsTests
{
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Confirmed_email_lists_seats_and_total()
    {
        var email = ReservationEmails.Confirmed(new ReservationConfirmed(ReservationId, Guid.NewGuid(), "alice@example.com", ["A-11", "A-12"], 90m, "EUR", Now));

        email.To.ShouldBe("alice@example.com");
        email.Subject.ShouldBe("Your tickets are confirmed");
        email.TextBody.ShouldContain("Seats: A-11, A-12");
        email.TextBody.ShouldContain("Total paid: 90.00 EUR");
    }

    [Fact]
    public void Cancelled_email_explains_that_seats_were_released()
    {
        var email = ReservationEmails.Cancelled(new ReservationCancelled(ReservationId, Guid.NewGuid(), "alice@example.com", "Card declined", Now));

        email.TextBody.ShouldContain("Card declined");
        email.TextBody.ShouldContain("seats were released");
    }

    [Fact]
    public void Expired_email_mentions_the_reservation()
    {
        ReservationEmails.Expired(new ReservationExpired(ReservationId, Guid.NewGuid(), "alice@example.com", Now))
            .TextBody.ShouldContain(ReservationId.ToString());
    }

    [Fact]
    public void Html_body_encodes_values_that_come_from_other_systems()
    {
        var email = ReservationEmails.Cancelled(new ReservationCancelled(ReservationId, Guid.NewGuid(), "a@example.com", "<script>x</script>", Now));

        email.HtmlBody.ShouldNotContain("<script>");
        email.HtmlBody.ShouldContain("&lt;script&gt;");
    }
}
