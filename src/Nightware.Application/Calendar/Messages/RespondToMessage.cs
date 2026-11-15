using Nightware.Application.Calendar.Scheduling;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Messages;

/// <summary>
/// Graph <c>POST messages/{id}/accept|tentativelyAccept|decline</c>: an RSVP from the invitation mail,
/// applied to the mailbox's copy of the meeting it is about.
/// </summary>
/// <remarks>
/// The organizer has nothing to answer, so that is a 400: NightMail then resolves the linked event,
/// sees <c>isOrganizer</c> and stops, as it does against Exchange.
/// </remarks>
public sealed class RespondToMessage(GetMessageEvent resolve, RespondToEvent respond)
{
    public async Task<Result> ExecuteAsync(MailboxRef mailbox, MessageId messageId, RsvpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var meeting = await resolve.ExecuteAsync(mailbox, messageId, cancellationToken).ConfigureAwait(false);
        if (!meeting.IsSuccess)
        {
            return meeting.Error;
        }

        if (meeting.Value.IsOrganizer)
        {
            return Error.Validation("The organizer of a meeting cannot respond to it.", "messageId");
        }

        return await respond.ExecuteAsync(mailbox, meeting.Value.Id, request, cancellationToken).ConfigureAwait(false);
    }
}
