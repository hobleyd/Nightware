using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Scheduling;

/// <summary>
/// Graph <c>POST events/{id}/accept|tentativelyAccept|decline</c>: the attendee's RSVP, on the series
/// or (for an occurrence id) one occurrence. A counter-proposal (<c>proposedNewTime</c>) only goes with
/// a decline or a tentative accept, as in Graph.
/// </summary>
public sealed class RespondToEvent(ISchedulingActions scheduling)
{
    public async Task<Result> ExecuteAsync(MailboxRef mailbox, EventId id, RsvpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Validate() is { } invalid)
        {
            return invalid;
        }

        return await scheduling.RespondAsync(mailbox, id, request.Response, string.IsNullOrEmpty(request.Comment) ? null : request.Comment, request.SendResponse, request.ProposedNewTime, cancellationToken)
            .ConfigureAwait(false);
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
