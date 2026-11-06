using TicketHub.BuildingBlocks.Messaging;
using TicketHub.Contracts.Booking;

namespace TicketHub.Notifications.Emails;

// The inbox (ADR-0003) makes a redelivered event a no-op. The one gap: if the process dies after SMTP accepted the
// email but before the inbox row commits, the redelivery sends it again. Email is at-least-once by nature; closing
// that gap would need idempotency support from the email provider.

public sealed class ReservationConfirmedHandler(IEmailSender sender) : IMessageHandler<ReservationConfirmed>
{
    public Task HandleAsync(ReservationConfirmed message, MessageContext context, CancellationToken cancellationToken) =>
        sender.SendAsync(ReservationEmails.Confirmed(message), cancellationToken);
}

public sealed class ReservationCancelledHandler(IEmailSender sender) : IMessageHandler<ReservationCancelled>
{
    public Task HandleAsync(ReservationCancelled message, MessageContext context, CancellationToken cancellationToken) =>
        sender.SendAsync(ReservationEmails.Cancelled(message), cancellationToken);
}

public sealed class ReservationExpiredHandler(IEmailSender sender) : IMessageHandler<ReservationExpired>
{
    public Task HandleAsync(ReservationExpired message, MessageContext context, CancellationToken cancellationToken) =>
        sender.SendAsync(ReservationEmails.Expired(message), cancellationToken);
}
