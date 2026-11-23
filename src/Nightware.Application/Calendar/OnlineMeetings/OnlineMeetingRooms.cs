using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.OnlineMeetings;

/// <summary>
/// Keeps an event's meeting room in step with the event, for the event use cases: a room is opened when an
/// event asks to be online, kept open when the meeting becomes a series, and closed when the organizer deletes or
/// cancels the meeting. A single meeting's room follows the event by itself (renamed and re-timed by the provider
/// from the stored event, ADR 0009 D7), so a rename or a move leaves it alone. A provider failure never fails the event write: the event is saved without a room
/// (the client sees no <c>joinUrl</c>), and a room that could not be closed is left (the adapter logs both).
/// </summary>
public sealed class OnlineMeetingRooms(IOnlineMeetings meetings, ICalendarReader reader)
{
    /// <summary>The event with a room's join URL when it asks to be online and has none yet; otherwise the event unchanged.</summary>
    public async Task<NewEvent> OpenForAsync(NewEvent newEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(newEvent);
        if (!newEvent.IsOnlineMeeting || newEvent.OnlineMeetingUrl is not null || !meetings.IsEnabled)
        {
            return newEvent;
        }

        var url = await OpenAsync(newEvent.Subject, newEvent.Start, newEvent.End, newEvent.Recurrence is not null, cancellationToken).ConfigureAwait(false);
        return url is null ? newEvent : newEvent with { OnlineMeetingUrl = url };
    }

    /// <summary>The event write failed: the room opened for it (if <see cref="OpenForAsync"/> opened one) goes too.</summary>
    public Task DiscardAsync(NewEvent asked, NewEvent written, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(written);
        return written.OnlineMeetingUrl is { } url && url != asked.OnlineMeetingUrl
            ? CloseAsync(url, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>
    /// The patch to write: with a new room's join URL when it turns an organizer's event online. When the event
    /// already has a room and the change makes it a series, the room is kept open (it has no single span to follow).
    /// </summary>
    public async Task<EventPatch> PrepareAsync(MailboxRef mailbox, EventId id, EventPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!meetings.IsEnabled || (patch.IsOnlineMeeting != true && patch.Recurrence is null))
        {
            return patch;
        }

        var current = await reader.GetAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return patch; // the write reports it
        }

        var stored = current.Value;
        if (stored.OnlineMeetingUrl is { } url)
        {
            if (patch.Recurrence is not null)
            {
                await meetings.KeepAsync(url, cancellationToken).ConfigureAwait(false);
            }

            return patch;
        }

        if (patch.IsOnlineMeeting != true || !stored.IsOrganizer)
        {
            return patch;
        }

        var recurring = patch.Recurrence is not null || (stored.Recurrence is not null && !patch.ClearRecurrence) || stored.SeriesMasterId is not null;
        var opened = await OpenAsync(patch.Subject ?? stored.Subject, patch.Start ?? stored.Start, patch.End ?? stored.End, recurring, cancellationToken)
            .ConfigureAwait(false);
        return opened is null ? patch : patch with { OnlineMeetingUrl = opened };
    }

    /// <summary>
    /// The room to close once the event at <paramref name="id"/> is deleted or cancelled: the meeting's own room
    /// when the mailbox organizes it. Never for one occurrence (the series keeps meeting) or an attendee's copy.
    /// </summary>
    public async Task<string?> RoomOfAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken)
    {
        if (!meetings.IsEnabled)
        {
            return null;
        }

        var current = await reader.GetAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        return current is { IsSuccess: true, Value: { IsOrganizer: true, SeriesMasterId: null, OnlineMeetingUrl: { } url } } ? url : null;
    }

    /// <summary>Closes the room at <paramref name="joinUrl"/>, if any; a failure leaves the room (the adapter logs it).</summary>
    public async Task CloseAsync(string? joinUrl, CancellationToken cancellationToken)
    {
        if (joinUrl is not null)
        {
            await meetings.CloseAsync(joinUrl, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> OpenAsync(string? subject, DateTimeTimeZone start, DateTimeTimeZone end, bool recurring, CancellationToken cancellationToken)
    {
        var opened = await meetings.OpenAsync(new OnlineMeetingRequest(subject, Instant(start), recurring ? null : Instant(end)), cancellationToken)
            .ConfigureAwait(false);
        return opened.IsSuccess ? opened.Value : null;
    }

    private static DateTimeOffset Instant(DateTimeTimeZone value) => GraphTimeZone.ToInstant(value.DateTime, value.TimeZone);
}
