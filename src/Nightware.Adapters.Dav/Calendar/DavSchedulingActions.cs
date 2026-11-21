using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Application.Calendar;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// <see cref="ISchedulingActions"/> over CalDAV. Each action edits the mailbox's copy of the meeting and
/// lets Sabre's scheduling plugin send the iTIP message (REPLY, CANCEL, REQUEST) by iMIP.
/// </summary>
/// <remarks>
/// An RSVP with <c>sendResponse: false</c> marks the organizer <c>SCHEDULE-AGENT=CLIENT</c>, so Sabre sends nothing.
/// Sabre's REPLY carries no comment and it has no COUNTER, so a comment or a proposed time for an organizer outside
/// Nextcloud is suppressed the same way and handed back as an <see cref="ItipMessage"/> for the caller to mail. For an
/// organizer inside Nextcloud Sabre delivers the reply to their calendar directly; a mail would not, so there the
/// comment and the proposal stay on the attendee's copy only (adapter README).
/// </remarks>
internal sealed class DavSchedulingActions(DavEventResources resources, DavCalendarReader reader, IcsEditor editor, Core.Discovery.IDavPrincipalDiscovery discovery)
    : ISchedulingActions
{
    public async Task<Result<ItipMessage?>> RespondAsync(
        MailboxRef mailbox, EventId id, RsvpResponse response, string? comment, bool sendResponse, TimeSlot? proposedNewTime, CancellationToken cancellationToken)
    {
        var meeting = await LoadAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!meeting.IsSuccess)
        {
            return meeting.Error;
        }

        var (stored, current) = meeting.Value;
        if (current.IsOrganizer)
        {
            return Error.Validation("The organizer of a meeting cannot respond to it.", "id");
        }

        // The façade answers an outside organizer itself only when Sabre's reply would lose something.
        var organizer = current.Organizer?.Address;
        var mail = sendResponse && (comment is not null || proposedNewTime is not null) && !string.IsNullOrEmpty(organizer)
            && await IsOutsideAsync(organizer, cancellationToken).ConfigureAwait(false);
        var edited = editor.SetParticipation(stored.Body, mailbox.Address, response, stored.Id.RecurrenceId, comment, serverReplies: sendResponse && !mail);
        if (!edited.IsSuccess)
        {
            return edited.Error;
        }

        ItipMessage? mailed = null;
        if (mail)
        {
            // From the edited copy: an occurrence answered on its own has its component (and RECURRENCE-ID) only now.
            var reply = editor.Reply(edited.Value, mailbox.Address, response, stored.Id.RecurrenceId, comment, proposedNewTime);
            if (!reply.IsSuccess)
            {
                return reply.Error;
            }

            mailed = new ItipMessage(organizer!, Subject(response, proposedNewTime, current.Subject), comment ?? string.Empty, reply.Value, proposedNewTime is null ? "REPLY" : "COUNTER");
        }

        var saved = await resources.SaveAsync(stored, edited.Value, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? mailed : saved.Error;
    }

    /// <summary>True when no Nextcloud principal has the address: the organizer gets iTIP by mail, not into a calendar here.</summary>
    private async Task<bool> IsOutsideAsync(string address, CancellationToken cancellationToken) =>
        await discovery.DiscoverAsync(new MailboxRef(address), cancellationToken).ConfigureAwait(false) is { IsSuccess: false, Error.Kind: ErrorKind.NotFound };

    /// <summary>As Outlook titles an answer: "Accepted: …", "Tentative: …", "Declined: …", "New Time Proposed: …".</summary>
    internal static string Subject(RsvpResponse response, TimeSlot? proposed, string? subject) =>
        (proposed is not null ? "New Time Proposed" : response switch
        {
            RsvpResponse.Accept => "Accepted",
            RsvpResponse.Decline => "Declined",
            _ => "Tentative",
        }) + ": " + (subject ?? string.Empty);

    /// <summary>
    /// Graph removes a cancelled meeting from the organizer's calendar and tells the attendees. Deleting
    /// the resource (or excluding the occurrence) is what makes Sabre send METHOD:CANCEL.
    /// </summary>
    public async Task<Result> CancelAsync(MailboxRef mailbox, EventId id, string? comment, CancellationToken cancellationToken)
    {
        var meeting = await LoadAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!meeting.IsSuccess)
        {
            return meeting.Error;
        }

        var (stored, current) = meeting.Value;
        if (!current.IsOrganizer)
        {
            return Error.Forbidden("Only the organizer can cancel a meeting.");
        }

        if (stored.Id.RecurrenceId is not { } occurrence)
        {
            return await resources.DeleteAsync(stored, cancellationToken).ConfigureAwait(false);
        }

        var edited = editor.ExcludeOccurrence(stored.Body, occurrence);
        return edited.IsSuccess
            ? await resources.SaveAsync(stored, edited.Value, cancellationToken).ConfigureAwait(false)
            : edited.Error;
    }

    /// <summary>
    /// The organizer's forward adds the recipients as attendees, and Sabre invites them. An attendee's
    /// copy cannot invite anyone through the calendar server, so that is a 403: NightMail then mails the
    /// invitation itself (its documented fallback).
    /// </summary>
    public async Task<Result> ForwardAsync(MailboxRef mailbox, EventId id, IReadOnlyList<EmailAddress> recipients, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        var meeting = await LoadAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!meeting.IsSuccess)
        {
            return meeting.Error;
        }

        var (stored, current) = meeting.Value;
        if (!current.IsOrganizer)
        {
            return Error.Forbidden("Only the organizer's calendar can forward a meeting; send the invitation by mail instead.");
        }

        var edited = editor.AddAttendees(stored.Body, recipients, stored.Id.RecurrenceId);
        return edited.IsSuccess
            ? await resources.SaveAsync(stored, edited.Value, cancellationToken).ConfigureAwait(false)
            : edited.Error;
    }

    private async Task<Result<(StoredEvent Stored, CalendarEvent Current)>> LoadAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken)
    {
        var stored = await resources.LoadAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        var current = reader.Read(stored.Value, mailbox);
        return current.IsSuccess ? (stored.Value, current.Value) : current.Error;
    }
}
