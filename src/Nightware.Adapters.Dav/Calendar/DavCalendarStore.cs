using System.Globalization;
using System.Xml.Linq;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Application.Calendar.Calendars;
using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// A mailbox's event calendars as CalDAV collections (ADR 0006 D6): create is <c>MKCALENDAR</c> in the owner's
/// calendar home, rename and recolour are <c>PROPPATCH</c>, delete is <c>DELETE</c>. A calendar's id is its
/// collection href, minted like every other DAV id. Events of a named calendar go through the same code as the
/// default calendar's.
/// </summary>
internal sealed class DavCalendarStore(
    DavCalendarScopes scopes, DavCalendarReader reader, DavCalendarWriter writer, DavEventResources resources, IDavClient dav, DavPaths paths) : ICalendarStore, ICalendarEventsIn
{
    public async Task<Result<IReadOnlyList<EventCalendar>>> ListAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var scope = await scopes.ResolveAsync(mailbox, cancellationToken).ConfigureAwait(false);
        return scope.Map(s => (IReadOnlyList<EventCalendar>)[.. s.Calendars
            .OrderBy(c => ReferenceEquals(c, s.Default) ? 0 : 1)
            .ThenBy(static c => c.Order ?? int.MaxValue)
            .Select(c => ToCalendar(c, s))]);
    }

    public async Task<Result<EventCalendar>> CreateAsync(MailboxRef mailbox, string name, string? hexColor, CancellationToken cancellationToken)
    {
        var scope = await scopes.ResolveAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        if (!scope.Value.Owner.IsCurrentUser || scope.Value.Owner.CalendarHomeSet is not { } home)
        {
            return Error.Forbidden($"Calendars can be added only to your own mailbox, not {mailbox.Address}.");
        }

        var href = home.TrimEnd('/') + "/nightware-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12] + "/";
        var created = await dav.MkCalendarAsync(href, name, ["VEVENT"], hexColor, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess ? await FindAsync(mailbox, DavItemId.Resource(paths.ToRelative(href)!), cancellationToken).ConfigureAwait(false) : created.Error;
    }

    public async Task<Result<EventCalendar>> UpdateAsync(MailboxRef mailbox, CalendarId id, string? name, string? hexColor, CancellationToken cancellationToken)
    {
        var calendar = await ResolveAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!calendar.IsSuccess)
        {
            return calendar.Error;
        }

        if (calendar.Value.Calendar.IsReadOnly)
        {
            return Error.Forbidden("The calendar is read-only.");
        }

        var set = new List<XElement>();
        var remove = new List<XName>();
        if (name is not null)
        {
            set.Add(new XElement(DavNames.DisplayName, name));
        }

        if (hexColor is { Length: > 0 })
        {
            set.Add(new XElement(DavNames.CalendarColor, hexColor));
        }
        else if (hexColor is not null)
        {
            remove.Add(DavNames.CalendarColor);
        }

        if (set.Count + remove.Count > 0)
        {
            var patched = await dav.PropPatchAsync(calendar.Value.Calendar.Href, set, remove, cancellationToken).ConfigureAwait(false);
            if (!patched.IsSuccess)
            {
                return patched.Error;
            }
        }

        return await FindAsync(mailbox, calendar.Value.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeleteAsync(MailboxRef mailbox, CalendarId id, CancellationToken cancellationToken)
    {
        var calendar = await ResolveAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!calendar.IsSuccess)
        {
            return calendar.Error;
        }

        return calendar.Value.Scope.Owner.IsCurrentUser
            ? await dav.DeleteAsync(calendar.Value.Calendar.Href, DavPrecondition.None, cancellationToken).ConfigureAwait(false)
            : Error.Forbidden("Only the owner can delete a calendar.");
    }

    public async Task<Result<Page<CalendarEvent>>> CalendarViewAsync(
        MailboxRef mailbox, CalendarId calendar, DateTimeOffset start, DateTimeOffset end, ItemQuery query, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(mailbox, calendar, cancellationToken).ConfigureAwait(false);
        return resolved.IsSuccess
            ? await reader.ViewAsync(resolved.Value.Calendar.Href, mailbox, start, end, query, cancellationToken).ConfigureAwait(false)
            : resolved.Error;
    }

    public async Task<Result<Page<CalendarEvent>>> ListAsync(MailboxRef mailbox, CalendarId calendar, ItemQuery query, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(mailbox, calendar, cancellationToken).ConfigureAwait(false);
        return resolved.IsSuccess
            ? await reader.ListInAsync(resolved.Value.Calendar.Href, mailbox, query, cancellationToken).ConfigureAwait(false)
            : resolved.Error;
    }

    public async Task<Result<CalendarEvent>> CreateAsync(MailboxRef mailbox, CalendarId calendar, NewEvent newEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(newEvent);
        var resolved = await ResolveAsync(mailbox, calendar, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return resolved.Error;
        }

        return resolved.Value.Calendar.IsReadOnly
            ? Error.Forbidden("The calendar is read-only.")
            : await writer.CreateInAsync(resolved.Value.Calendar.Href, resolved.Value.Scope.Owner.DisplayName, mailbox, newEvent, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<Page<CalendarEvent>>> InstancesAsync(
        MailboxRef mailbox, EventId series, DateTimeOffset start, DateTimeOffset end, ItemQuery query, CancellationToken cancellationToken)
    {
        // The series' resource names its calendar; its expansion there, kept to this series, is the answer.
        var stored = await resources.LoadAsync(mailbox, series, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        var calendarHref = paths.ToAbsolutePath(stored.Value.Id.Href[..(stored.Value.Id.Href.LastIndexOf('/') + 1)])!;
        var view = await reader.ViewAsync(calendarHref, mailbox, start, end, ItemQuery.Default with { Top = int.MaxValue }, cancellationToken).ConfigureAwait(false);
        if (!view.IsSuccess)
        {
            return view.Error;
        }

        var master = stored.Value.Id.Master.ToEventId();
        var instances = view.Value.Items.Where(e => e.SeriesMasterId == master).ToList();
        return OffsetPaging.Slice(instances, query, DavCalendarReader.DefaultPageSize);
    }

    private async Task<Result<EventCalendar>> FindAsync(MailboxRef mailbox, DavItemId id, CancellationToken cancellationToken)
    {
        var all = await ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        return all.Bind(list => list.FirstOrDefault(c => c.Id.Value == id.ToString()) is { } found
            ? Result<EventCalendar>.Success(found)
            : Error.NotFound("The specified calendar could not be found."));
    }

    private async Task<Result<(CalendarScope Scope, DavCalendar Calendar, DavItemId Id)>> ResolveAsync(MailboxRef mailbox, CalendarId id, CancellationToken cancellationToken)
    {
        var parsed = DavItemId.Parse(id.Value, "id");
        if (!parsed.IsSuccess)
        {
            return parsed.Error;
        }

        var scope = await scopes.ResolveAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        var calendar = scope.Value.Calendars.FirstOrDefault(c => string.Equals(paths.ToRelative(c.Href), parsed.Value.Href, StringComparison.Ordinal));
        return calendar is null
            ? Error.NotFound("The specified calendar could not be found.")
            : (scope.Value, calendar, parsed.Value);
    }

    private EventCalendar ToCalendar(DavCalendar calendar, CalendarScope scope)
    {
        var owner = scope.Owner.EmailAddresses is [var address, ..] ? new EmailAddress(address, scope.Owner.DisplayName ?? address) : null;
        var isDefault = ReferenceEquals(calendar, scope.Default);
        return new EventCalendar(new CalendarId(DavItemId.Resource(paths.ToRelative(calendar.Href)!).ToString()), calendar.DisplayName)
        {
            HexColor = calendar.Color is { Length: >= 7 } color ? color[..7].ToUpperInvariant() : null,
            IsDefault = isDefault,
            CanEdit = !calendar.IsReadOnly,
            IsSharedWithMe = !scope.Owner.IsCurrentUser,
            IsRemovable = !isDefault && scope.Owner.IsCurrentUser,
            Owner = owner,
        };
    }
}
