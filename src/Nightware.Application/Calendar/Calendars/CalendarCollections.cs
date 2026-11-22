using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Calendars;

/// <summary>A mailbox's event calendars (ADR 0006 D6).</summary>
public interface ICalendarStore
{
    /// <summary>Every event calendar of the mailbox, the default first.</summary>
    Task<Result<IReadOnlyList<EventCalendar>>> ListAsync(MailboxRef mailbox, CancellationToken cancellationToken);

    Task<Result<EventCalendar>> CreateAsync(MailboxRef mailbox, string name, string? hexColor, CancellationToken cancellationToken);

    /// <summary>Null members are left as they are.</summary>
    Task<Result<EventCalendar>> UpdateAsync(MailboxRef mailbox, CalendarId id, string? name, string? hexColor, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef mailbox, CalendarId id, CancellationToken cancellationToken);
}

/// <summary>Events of one named calendar (Graph <c>calendars/{id}/…</c>); the default calendar's go through <see cref="ICalendarReader"/>.</summary>
public interface ICalendarEventsIn
{
    Task<Result<Page<CalendarEvent>>> CalendarViewAsync(MailboxRef mailbox, CalendarId calendar, DateTimeOffset start, DateTimeOffset end, ItemQuery query, CancellationToken cancellationToken);

    /// <summary>Unexpanded events: series masters and single events.</summary>
    Task<Result<Page<CalendarEvent>>> ListAsync(MailboxRef mailbox, CalendarId calendar, ItemQuery query, CancellationToken cancellationToken);

    Task<Result<CalendarEvent>> CreateAsync(MailboxRef mailbox, CalendarId calendar, NewEvent newEvent, CancellationToken cancellationToken);

    /// <summary>A series' occurrences and exceptions overlapping [start, end) (Graph <c>events/{id}/instances</c>).</summary>
    Task<Result<Page<CalendarEvent>>> InstancesAsync(MailboxRef mailbox, EventId series, DateTimeOffset start, DateTimeOffset end, ItemQuery query, CancellationToken cancellationToken);
}

/// <summary>Graph's <c>calendarColor</c> names, which Nextcloud keeps as hex.</summary>
public static class CalendarColors
{
    public const string Auto = "auto";

    private static readonly Dictionary<string, string> Hex = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lightBlue"] = "#A6D1F5",
        ["lightGreen"] = "#87D28E",
        ["lightOrange"] = "#FCAB73",
        ["lightGray"] = "#C0C0C0",
        ["lightYellow"] = "#F4E3A0",
        ["lightTeal"] = "#8FD8D0",
        ["lightPink"] = "#F5A3C7",
        ["lightBrown"] = "#D2B48C",
        ["lightRed"] = "#F1878A",
    };

    /// <summary>The hex for a Graph colour name; <c>auto</c> is no colour of its own.</summary>
    public static Result<string?> ToHex(string color) =>
        string.Equals(color, Auto, StringComparison.OrdinalIgnoreCase)
            ? Result<string?>.Success(null)
            : Hex.TryGetValue(color, out var hex) ? hex : Error.Validation($"'{color}' is not a calendar colour.", "color");

    /// <summary>The Graph colour name for a stored hex (<c>auto</c> unless it is one of Graph's).</summary>
    public static string FromHex(string? hex) =>
        hex is { Length: >= 7 } ? Hex.FirstOrDefault(p => string.Equals(p.Value, hex[..7], StringComparison.OrdinalIgnoreCase)).Key ?? Auto : Auto;
}

/// <summary>
/// Graph <c>calendarGroups</c>: Nextcloud has no groups, so every calendar is in one synthetic "My Calendars".
/// </summary>
public static class CalendarGroups
{
    public const string DefaultId = "calendargroup-default";
    public const string DefaultName = "My Calendars";

    /// <summary>Outlook's class id for calendar groups of calendars.</summary>
    public const string ClassId = "0006f0b7-0000-0000-c000-000000000046";
}

/// <summary>Graph <c>GET calendars</c>.</summary>
public sealed class ListCalendars(ICalendarStore store)
{
    public Task<Result<IReadOnlyList<EventCalendar>>> ExecuteAsync(MailboxRef mailbox, CancellationToken cancellationToken) =>
        store.ListAsync(mailbox, cancellationToken);
}

/// <summary>Graph <c>GET calendars/{id}</c>, and <c>GET calendar</c> (the default) when <paramref name="id"/> is null.</summary>
public sealed class GetCalendar(ICalendarStore store)
{
    public async Task<Result<EventCalendar>> ExecuteAsync(MailboxRef mailbox, CalendarId? id, CancellationToken cancellationToken)
    {
        var calendars = await store.ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        return calendars.Bind(list => (id is { } wanted ? list.FirstOrDefault(c => c.Id == wanted) : list.FirstOrDefault(static c => c.IsDefault)) is { } found
            ? Result<EventCalendar>.Success(found)
            : Error.NotFound("The specified calendar could not be found."));
    }
}

/// <summary>Graph <c>POST calendars</c>.</summary>
public sealed class CreateCalendar(ICalendarStore store)
{
    public async Task<Result<EventCalendar>> ExecuteAsync(MailboxRef mailbox, string? name, string? color, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Error.Validation("A calendar needs a name.", "name");
        }

        var hex = color is null ? Result<string?>.Success(null) : CalendarColors.ToHex(color);
        return hex.IsSuccess ? await store.CreateAsync(mailbox, name.Trim(), hex.Value, cancellationToken).ConfigureAwait(false) : hex.Error;
    }
}

/// <summary>Graph <c>PATCH calendars/{id}</c>: name and colour.</summary>
public sealed class UpdateCalendar(ICalendarStore store)
{
    public async Task<Result<EventCalendar>> ExecuteAsync(MailboxRef mailbox, CalendarId id, string? name, string? color, CancellationToken cancellationToken)
    {
        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            return Error.Validation("A calendar needs a name.", "name");
        }

        var hex = color is null ? Result<string?>.Success(null) : CalendarColors.ToHex(color);
        if (!hex.IsSuccess)
        {
            return hex.Error;
        }

        // "auto" clears the colour: Nextcloud then shows its default.
        return await store.UpdateAsync(mailbox, id, name?.Trim(), color is null ? null : hex.Value ?? string.Empty, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>DELETE calendars/{id}</c>; the default calendar cannot be deleted, as in Exchange.</summary>
public sealed class DeleteCalendar(ICalendarStore store)
{
    public async Task<Result> ExecuteAsync(MailboxRef mailbox, CalendarId id, CancellationToken cancellationToken)
    {
        var calendars = await store.ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!calendars.IsSuccess)
        {
            return calendars.Error;
        }

        var calendar = calendars.Value.FirstOrDefault(c => c.Id == id);
        if (calendar is null)
        {
            return Error.NotFound("The specified calendar could not be found.");
        }

        return calendar.IsDefault
            ? new Error(ErrorKind.Validation, "ErrorCannotDeleteDefaultCalendar", "The default calendar cannot be deleted.")
            : await store.DeleteAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>GET calendarGroups/{id}/calendars</c>: the one group holds every calendar.</summary>
public sealed class ListGroupCalendars(ICalendarStore store)
{
    public async Task<Result<IReadOnlyList<EventCalendar>>> ExecuteAsync(MailboxRef mailbox, string groupId, CancellationToken cancellationToken) =>
        groupId == CalendarGroups.DefaultId
            ? await store.ListAsync(mailbox, cancellationToken).ConfigureAwait(false)
            : Error.NotFound("The specified calendar group could not be found.");
}

/// <summary>
/// Graph <c>GET calendars/{id}/events</c> and <c>calendarView</c>, and <c>POST calendars/{id}/events</c> (an online
/// meeting gets its room first, as <see cref="Events.CreateEvent"/>'s does).
/// </summary>
public sealed class CalendarEventsIn(ICalendarEventsIn events, OnlineMeetings.OnlineMeetingRooms rooms)
{
    public Task<Result<Page<CalendarEvent>>> ListAsync(MailboxRef mailbox, CalendarId calendar, ItemQuery query, CancellationToken cancellationToken) =>
        events.ListAsync(mailbox, calendar, query, cancellationToken);

    public Task<Result<Page<CalendarEvent>>> ViewAsync(MailboxRef mailbox, CalendarId calendar, DateTimeOffset start, DateTimeOffset end, ItemQuery query, CancellationToken cancellationToken) =>
        CalendarViewLimits.Check(start, end) is { } tooLong
            ? Task.FromResult<Result<Page<CalendarEvent>>>(tooLong)
            : events.CalendarViewAsync(mailbox, calendar, start, end, query, cancellationToken);

    public async Task<Result<CalendarEvent>> CreateAsync(MailboxRef mailbox, CalendarId calendar, NewEvent newEvent, CancellationToken cancellationToken)
    {
        var online = await rooms.OpenForAsync(newEvent, cancellationToken).ConfigureAwait(false);
        var created = await events.CreateAsync(mailbox, calendar, online, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            await rooms.DiscardAsync(newEvent, online, cancellationToken).ConfigureAwait(false);
        }

        return created;
    }

    public Task<Result<Page<CalendarEvent>>> InstancesAsync(MailboxRef mailbox, EventId series, DateTimeOffset start, DateTimeOffset end, ItemQuery query, CancellationToken cancellationToken) =>
        CalendarViewLimits.Check(start, end) is { } tooLong
            ? Task.FromResult<Result<Page<CalendarEvent>>>(tooLong)
            : events.InstancesAsync(mailbox, series, start, end, query, cancellationToken);
}

/// <summary>
/// Graph <c>calendarView/delta</c> on the default calendar (ADR 0006 D6): the window's occurrences first, then
/// what changed. The window rides in the token, as Graph freezes it into the delta link.
/// </summary>
public interface ICalendarViewDelta
{
    /// <param name="start">Required (with <paramref name="end"/>) without a token; ignored with one.</param>
    Task<Result<DeltaPage<CalendarEvent>>> DeltaAsync(
        MailboxRef mailbox, DateTimeOffset? start, DateTimeOffset? end, DeltaToken? token, CancellationToken cancellationToken);
}

/// <summary>Graph <c>GET calendarView/delta</c>.</summary>
public sealed class SyncCalendarView(ICalendarViewDelta delta)
{
    public Task<Result<DeltaPage<CalendarEvent>>> ExecuteAsync(
        MailboxRef mailbox, DateTimeOffset? start, DateTimeOffset? end, DeltaToken? token, CancellationToken cancellationToken)
    {
        if (token is null && (start is null || end is null))
        {
            return Task.FromResult<Result<DeltaPage<CalendarEvent>>>(
                Error.Validation("This request requires a time window specified by the query string parameters startDateTime and endDateTime.", "startDateTime"));
        }

        if (token is null && CalendarViewLimits.Check(start!.Value, end!.Value) is { } tooLong)
        {
            return Task.FromResult<Result<DeltaPage<CalendarEvent>>>(tooLong);
        }

        return delta.DeltaAsync(mailbox, start, end, token, cancellationToken);
    }
}
