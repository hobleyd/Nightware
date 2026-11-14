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
/// What Sabre's messages cannot carry is dropped, and documented in the adapter README: the RSVP and
/// cancellation comments (kept on the stored copy only), <c>sendResponse: false</c> (Sabre always replies),
/// and a counter-proposal's new time (Sabre has no iTIP COUNTER).
/// </remarks>
internal sealed class DavSchedulingActions(DavEventResources resources, DavCalendarReader reader, IcsEditor editor) : ISchedulingActions
{
    public async Task<Result> RespondAsync(MailboxRef mailbox, EventId id, RsvpResponse response, string? comment, bool sendResponse, TimeSlot? proposedNewTime, CancellationToken cancellationToken)
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

        var edited = editor.SetParticipation(stored.Body, mailbox.Address, response, stored.Id.RecurrenceId, comment);
        return edited.IsSuccess
            ? await resources.SaveAsync(stored, edited.Value, cancellationToken).ConfigureAwait(false)
            : edited.Error;
    }

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
