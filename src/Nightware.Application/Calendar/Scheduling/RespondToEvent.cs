using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Scheduling;

/// <summary>
/// Graph <c>POST events/{id}/accept|tentativelyAccept|decline</c>: the attendee's RSVP, on the series
/// or (for an occurrence id) one occurrence. A counter-proposal (<c>proposedNewTime</c>) only goes with
/// a decline or a tentative accept, as in Graph. When the calendar server can't carry the reply (a comment or
/// a proposal to an outside organizer) the adapter hands it back and it is mailed from the mailbox.
/// </summary>
public sealed class RespondToEvent(ISchedulingActions scheduling, IItipMail itip)
{
    public async Task<Result> ExecuteAsync(MailboxRef mailbox, EventId id, RsvpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Validate() is { } invalid)
        {
            return invalid;
        }

        var responded = await scheduling.RespondAsync(mailbox, id, request.Response, string.IsNullOrEmpty(request.Comment) ? null : request.Comment, request.SendResponse, request.ProposedNewTime, cancellationToken)
            .ConfigureAwait(false);
        if (!responded.IsSuccess)
        {
            return responded.Error;
        }

        return responded.Value is { } message ? await itip.SendAsync(mailbox, message, cancellationToken).ConfigureAwait(false) : Result.Success();
    }
}

/// <summary>An RSVP as Graph's action body carries it.</summary>
/// <param name="Comment">Empty means none.</param>
public sealed record RsvpRequest(RsvpResponse Response, string? Comment, bool SendResponse, TimeSlot? ProposedNewTime = null)
{
    internal Error? Validate()
    {
        if (ProposedNewTime is not { } proposal)
        {
            return null;
        }

        return Response == RsvpResponse.Accept
            ? Error.Validation("A new time can only be proposed when declining or tentatively accepting.", "proposedNewTime")
            : CalendarRules.Ordered(proposal.Start, proposal.End, "proposedNewTime", allowEmpty: false);
    }
}
