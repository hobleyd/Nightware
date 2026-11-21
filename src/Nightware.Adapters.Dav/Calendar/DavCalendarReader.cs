using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Calendar;
using Nightware.Application.Common.Query;
using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// <see cref="ICalendarReader"/> over CalDAV. <c>calendarView</c> reads the default calendar
/// (<see cref="DavCalendarScopes.DefaultAsync"/>, remembered) with Sabre's server-side expansion: one REPORT when warm; single events, masters and occurrences are read from their resource.
/// </summary>
internal sealed class DavCalendarReader(
    DavCalendarScopes scopes, DavEventResources resources, IDavClient dav, DavPaths paths, IcalNetCalendarCodec codec, IcsEditor editor)
    : ICalendarReader
{
    /// <summary>Graph's default page size for events and calendarView.</summary>
    internal const int DefaultPageSize = 10;

    public async Task<Result<Page<CalendarEvent>>> CalendarViewAsync(MailboxRef mailbox, DateTimeOffset start, DateTimeOffset end, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (EventFilters.Unsupported(query) is { } unsupported)
        {
            return unsupported;
        }

        // Sabre expands without limit; some callers take the window from an invitation anyone can send.
        if (CalendarViewLimits.Check(start, end) is { } tooLong)
        {
            return tooLong;
        }

        for (var attempt = 0; ; attempt++)
        {
            var calendar = await scopes.DefaultAsync(mailbox, cancellationToken).ConfigureAwait(false);
            if (!calendar.IsSuccess)
            {
                return calendar.Error;
            }

            if (calendar.Value is null)
            {
                return OffsetPaging.Slice<CalendarEvent>([], query, DefaultPageSize);
            }

            var view = await ViewAsync(calendar.Value.Href, mailbox, start, end, query, cancellationToken).ConfigureAwait(false);
            if (view is { IsSuccess: false, Error.Kind: ErrorKind.NotFound or ErrorKind.Forbidden } && attempt == 0)
            {
                // The remembered default calendar was deleted or unshared since: look again.
                await scopes.ForgetDefault(mailbox, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return view;
        }
    }

    /// <summary>The expanded occurrences of one calendar overlapping [start, end).</summary>
    internal async Task<Result<Page<CalendarEvent>>> ViewAsync(
        string calendarHref, MailboxRef mailbox, DateTimeOffset start, DateTimeOffset end, ItemQuery query, CancellationToken cancellationToken)
    {
        if (EventFilters.Unsupported(query) is { } unsupported)
        {
            return unsupported;
        }

        var range = new DavTimeRange(start, end);
        var report = DavRequestBodies.CalendarQuery(new CalendarQuery("VEVENT") { TimeRange = range, Expand = range });
        var expanded = await dav.ReportAsync(calendarHref, DavDepth.One, report, cancellationToken).ConfigureAwait(false);
        if (!expanded.IsSuccess)
        {
            return expanded.Error;
        }

        var instances = ParseAll(expanded.Value, mailbox, isExpanded: true)
            .SelectMany(r => r.Events.Select(e => (r.Href, Event: e)))
            .ToList();
        var exceptions = await StoredExceptionsAsync(
            calendarHref, instances.Where(i => i.Event.OriginalStart is not null).Select(i => i.Href).Distinct(StringComparer.Ordinal).ToList(), mailbox, cancellationToken)
            .ConfigureAwait(false);
        if (!exceptions.IsSuccess)
        {
            return exceptions.Error;
        }

        var view = instances
            .Select(i => Classify(i.Event, i.Href, exceptions.Value))
            .OrderBy(e => GraphTimeZone.ToInstant(e.Start.DateTime, e.Start.TimeZone))
            .ThenBy(e => e.Subject, StringComparer.Ordinal)
            .ToList();
        return OffsetPaging.Slice(view, query, DefaultPageSize);
    }

    public async Task<Result<CalendarEvent>> GetAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken)
    {
        var stored = await resources.LoadAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        return stored.IsSuccess ? Read(stored.Value, mailbox) : stored.Error;
    }

    public async Task<Result<Page<CalendarEvent>>> ListAsync(MailboxRef mailbox, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var uid = EventFilters.UidOf(query.Filter);
        if (!uid.IsSuccess)
        {
            return uid.Error;
        }

        var scope = await scopes.ResolveAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        // Unfiltered, this is Graph's /events: the default calendar's events, orderable by start.
        if (uid.Value is null)
        {
            return scope.Value.Default is { } defaultCalendar
                ? await ListInAsync(defaultCalendar.Href, mailbox, query, cancellationToken).ConfigureAwait(false)
                : OffsetPaging.Slice<CalendarEvent>([], query, DefaultPageSize);
        }

        if (query.Search is not null || query.OrderBy.Count > 0)
        {
            return Error.Validation("Events filtered by iCalUId support only $select, $top and $skip.", query.Search is not null ? "$search" : "$orderby");
        }

        // A UID is looked up in every calendar of the mailbox: an invitation may have been filed in any.
        var calendars = scope.Value.Calendars;
        var found = new List<CalendarEvent>();
        foreach (var candidate in calendars)
        {
            var report = DavRequestBodies.CalendarQuery(new CalendarQuery("VEVENT") { Uid = uid.Value });
            var listing = await dav.ReportAsync(candidate.Href, DavDepth.One, report, cancellationToken).ConfigureAwait(false);
            if (!listing.IsSuccess)
            {
                return listing.Error;
            }

            found.AddRange(ParseAll(listing.Value, mailbox, isExpanded: false).Select(r => Primary(r.Events)).OfType<CalendarEvent>());
        }

        var ordered = found.OrderBy(e => GraphTimeZone.ToInstant(e.Start.DateTime, e.Start.TimeZone)).ToList();
        return OffsetPaging.Slice(ordered, query, DefaultPageSize);
    }

    /// <summary>The unexpanded events of one calendar (masters and single events), by start or as <paramref name="query"/> orders.</summary>
    internal async Task<Result<Page<CalendarEvent>>> ListInAsync(string calendarHref, MailboxRef mailbox, ItemQuery query, CancellationToken cancellationToken)
    {
        if (query.Search is not null || query.Filter is not null)
        {
            return Error.Validation("A calendar's events support $select, $orderby on start, $top and $skip.", query.Search is not null ? "$search" : "$filter");
        }

        var descending = EventFilters.StartDescending(query);
        if (!descending.IsSuccess)
        {
            return descending.Error;
        }

        var listing = await dav.ReportAsync(calendarHref, DavDepth.One, DavRequestBodies.CalendarQuery(new CalendarQuery("VEVENT")), cancellationToken).ConfigureAwait(false);
        if (!listing.IsSuccess)
        {
            return listing.Error;
        }

        var events = ParseAll(listing.Value, mailbox, isExpanded: false).Select(r => Primary(r.Events)).OfType<CalendarEvent>();
        var ordered = descending.Value
            ? events.OrderByDescending(e => GraphTimeZone.ToInstant(e.Start.DateTime, e.Start.TimeZone)).ToList()
            : events.OrderBy(e => GraphTimeZone.ToInstant(e.Start.DateTime, e.Start.TimeZone)).ToList();
        return OffsetPaging.Slice(ordered, query, DefaultPageSize);
    }

    /// <summary>
    /// The event an id names within its stored resource. An occurrence id resolves to its exception
    /// component, or, for an occurrence the organizer never modified, to the instance the master produces
    /// there (typed <see cref="EventType.Occurrence"/>). An occurrence removed with EXDATE is NotFound.
    /// </summary>
    internal Result<CalendarEvent> Read(StoredEvent stored, MailboxRef mailbox)
    {
        var options = new CalendarParseOptions { Href = stored.Id.Href, Mailbox = mailbox };
        var document = codec.Parse(stored.Body, options);
        if (!document.IsSuccess)
        {
            return document.Error;
        }

        if (stored.Id.RecurrenceId is not { } occurrence)
        {
            return Primary(document.Value.Events) is { } primary
                ? primary
                : Error.NotFound("The resource holds no event.");
        }

        var eventId = stored.Id.ToEventId();
        var own = document.Value.Events.FirstOrDefault(e => e.Id == eventId);
        if (own is { IsCancelled: false })
        {
            return own;
        }

        // Either unmodified, or cancelled: an exception component may carry STATUS:CANCELLED, while an
        // EXDATE (which the codec also surfaces as a cancelled exception) means the occurrence is gone.
        var materialized = editor.MaterializeOccurrence(stored.Body, occurrence);
        if (!materialized.IsSuccess)
        {
            return materialized.Error;
        }

        var instance = codec.Parse(materialized.Value, options).Bind(d =>
            d.Events.FirstOrDefault(e => e.Id == eventId && e.Type == EventType.Exception) is { } found
                ? Result<CalendarEvent>.Success(found)
                : Error.NotFound("The specified occurrence was not found."));
        return own is null ? instance.Map(e => e with { Type = EventType.Occurrence }) : instance;
    }

    /// <summary>
    /// The RECURRENCE-IDs each series has an exception component for. Sabre's expansion writes every
    /// instance with a RECURRENCE-ID, so only the stored resource tells a modified one from the rest.
    /// </summary>
    private async Task<Result<Dictionary<string, HashSet<DateTimeOffset>>>> StoredExceptionsAsync(
        string calendarHref, List<string> hrefs, MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var exceptions = new Dictionary<string, HashSet<DateTimeOffset>>(StringComparer.Ordinal);
        if (hrefs.Count == 0)
        {
            return exceptions;
        }

        var multiget = await dav.ReportAsync(calendarHref, DavDepth.One, DavRequestBodies.CalendarMultiget(hrefs.Select(h => paths.RootPath + h)), cancellationToken)
            .ConfigureAwait(false);
        if (!multiget.IsSuccess)
        {
            return multiget.Error;
        }

        foreach (var (href, events) in ParseAll(multiget.Value, mailbox, isExpanded: false))
        {
            exceptions[href] = events
                .Where(e => e is { Type: EventType.Exception, OriginalStart: not null, IsCancelled: false })
                .Select(e => e.OriginalStart!.Value)
                .ToHashSet();
        }

        return exceptions;
    }

    private static CalendarEvent Classify(CalendarEvent instance, string href, Dictionary<string, HashSet<DateTimeOffset>> exceptions)
    {
        if (instance.OriginalStart is not { } originalStart)
        {
            return instance;
        }

        var isException = exceptions.TryGetValue(href, out var modified) && modified.Contains(originalStart);
        return instance with { Type = isException ? EventType.Exception : EventType.Occurrence, Recurrence = null };
    }

    /// <summary>
    /// What Graph lists for a resource: its master or single event; for an attendee invited to single
    /// occurrences only (no master in the resource), the first of those that is not cancelled.
    /// </summary>
    private static CalendarEvent? Primary(IReadOnlyList<CalendarEvent> events) =>
        events.FirstOrDefault(e => e.Type is EventType.SingleInstance or EventType.SeriesMaster)
        ?? events.FirstOrDefault(e => !e.IsCancelled);

    private IEnumerable<(string Href, IReadOnlyList<CalendarEvent> Events)> ParseAll(DavMultistatus multistatus, MailboxRef mailbox, bool isExpanded)
    {
        foreach (var response in multistatus.Responses.Where(r => r.IsFound))
        {
            if (response.CalendarData is not { } data || paths.ToRelative(response.Href) is not { } href)
            {
                continue;
            }

            // One unreadable resource must not hide the rest of the calendar.
            var document = codec.Parse(data, new CalendarParseOptions { Href = href, Mailbox = mailbox, Expanded = isExpanded });
            if (document.IsSuccess)
            {
                yield return (href, document.Value.Events);
            }
        }
    }
}
