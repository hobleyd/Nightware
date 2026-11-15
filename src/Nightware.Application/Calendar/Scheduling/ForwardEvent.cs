using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Scheduling;

/// <summary>Graph <c>POST events/{id}/forward</c>: invites further people to the meeting.</summary>
public sealed class ForwardEvent(ISchedulingActions scheduling)
{
    public async Task<Result> ExecuteAsync(MailboxRef mailbox, EventId id, IReadOnlyList<EmailAddress> recipients, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Count == 0)
        {
            return Error.Validation("At least one recipient is required.", "toRecipients");
        }

        if (CalendarRules.Addresses(recipients, "toRecipients") is { } invalid)
        {
            return invalid;
        }

        return await scheduling.ForwardAsync(mailbox, id, recipients, string.IsNullOrEmpty(comment) ? null : comment, cancellationToken)
            .ConfigureAwait(false);
    }
}
