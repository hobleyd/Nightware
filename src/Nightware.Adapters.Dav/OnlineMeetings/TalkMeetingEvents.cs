using System.Globalization;
using System.Xml.Linq;

using Ical.Net.Serialization;

using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Calendar;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Adapters.Dav.Talk;
using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Nightware.Adapters.Dav.OnlineMeetings;

/// <summary>A meeting's event in the caller's calendar, as read for an edit.</summary>
/// <param name="IsRecord">True for the event Nightware made as the meeting's record (no attendees, marked); false for a calendar event made online.</param>
internal sealed record MeetingEvent(string Href, string Body, string? ETag, bool IsRecord);

/// <summary>
/// The calendar event that drives a meeting's room (ADR 0009 D7, 2026-11-23). Talk renames and re-times a room bound
/// to a span whenever its owner's event with the room's link in <c>LOCATION</c> changes, synchronously within the
/// CalDAV write, and refuses to do either over its own API. So a meeting with a span gets an event in the organiser's
/// default calendar (subject, span, the link in <c>LOCATION</c> and <c>URL</c>, nobody invited: it is the meeting's
/// record, marked <c>X-NIGHTWARE-ONLINE-MEETING</c>), and changing the meeting changes that event. A calendar event
/// made online (phase 22) already is such an event and is found the same way. Read and written as the caller.
/// </summary>
internal sealed class TalkMeetingEvents(
    IDavClient dav, IDavPrincipalDiscovery discovery, DavCalendarScopes scopes, IcalNetCalendarCodec codec, IcsEditor editor, TimeProvider time,
    IOptions<TalkOptions> options)
{
    /// <summary>Marks the event Nightware made as a meeting's record; its value is the room token.</summary>
    public const string RecordProperty = "X-NIGHTWARE-ONLINE-MEETING";

    /// <summary>A new event in the caller's default calendar for the room <paramref name="token"/>, which Talk binds to it.</summary>
    public async Task<Result> CreateAsync(string token, string subject, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var scope = await OwnScopeAsync(cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        if (scope.Value.Scope.Default is not { } calendar)
        {
            return Error.Forbidden("You have no calendar to keep the meeting in.");
        }

        var now = time.GetUtcNow();
        var record = new CalendarEvent(new EventId(string.Empty), subject, Utc(start), Utc(end))
        {
            ICalUId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            IsOnlineMeeting = true,
            OnlineMeetingUrl = TalkRooms.JoinUrl(options.Value, token),
            CreatedDateTime = now,
            LastModifiedDateTime = now,
        };
        var loaded = IcalNetCalendarCodec.Load(codec.Serialize(new CalendarDocument(null, [record])));
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        loaded.Value.Events[0]!.AddProperty(RecordProperty, token);
        var ics = new CalendarSerializer().SerializeToString(loaded.Value) ?? string.Empty;
        var href = calendar.Href + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".ics";
        var put = await dav.PutAsync(href, ics, DavEventResources.ContentType, DavPrecondition.CreateOnly, cancellationToken).ConfigureAwait(false);
        return put.ToResult();
    }

    /// <summary>
    /// The caller's own event that carries the room's link (in <c>LOCATION</c>, as Talk looks for it, or in <c>URL</c>,
    /// as events made before the link moved into <c>LOCATION</c> have it); null when there is none.
    /// </summary>
    public async Task<Result<MeetingEvent?>> FindAsync(string token, CancellationToken cancellationToken)
    {
        var scope = await OwnScopeAsync(cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            // No calendar of their own: no event to find.
            return scope.Error.Kind is ErrorKind.NotFound or ErrorKind.Forbidden ? Result<MeetingEvent?>.Success(null) : scope.Error;
        }

        var link = TalkRooms.JoinUrl(options.Value, token);
        foreach (var property in (string[])["LOCATION", "URL"])
        {
            foreach (var calendar in scope.Value.Scope.Calendars.Where(c => !c.IsReadOnly))
            {
                var listing = await dav.ReportAsync(calendar.Href, DavDepth.One, LinkQuery(property, link), cancellationToken).ConfigureAwait(false);
                if (!listing.IsSuccess)
                {
                    return listing.Error;
                }

                // A text-match is a substring match (call/abcd matches call/abcdefgh): only the exact room counts.
                foreach (var response in listing.Value.Responses.Where(r => r.IsFound))
                {
                    if (response.CalendarData is { } body && IsFor(body, token))
                    {
                        return new MeetingEvent(response.Href, body, response.ETag, IsRecordOf(body, token));
                    }
                }
            }
        }

        return Result<MeetingEvent?>.Success(null);
    }

    /// <summary>
    /// Changes the event's subject and/or span, as the organizer: Talk renames and re-times the room within the write.
    /// A calendar meeting's attendees are sent the change, as for any edit of the event.
    /// </summary>
    public async Task<Result> UpdateAsync(MeetingEvent meeting, string? subject, (DateTimeOffset Start, DateTimeOffset End)? span, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        var scope = await OwnScopeAsync(cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        var patch = new EventPatch { Subject = subject, Start = span is { } s ? Utc(s.Start) : null, End = span is { } e ? Utc(e.End) : null };
        var edited = editor.ApplyPatch(meeting.Body, patch, null, scope.Value.Address);
        if (!edited.IsSuccess)
        {
            return edited.Error;
        }

        var precondition = meeting.ETag is { } etag ? DavPrecondition.Matching(etag) : DavPrecondition.None;
        var put = await dav.PutAsync(meeting.Href, edited.Value, DavEventResources.ContentType, precondition, cancellationToken).ConfigureAwait(false);
        return put.ToResult();
    }

    /// <summary>Deletes the meeting's record. A calendar event made online is the caller's meeting, not the room's record, and stays.</summary>
    public async Task<Result> DeleteAsync(MeetingEvent meeting, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        if (!meeting.IsRecord)
        {
            return Result.Success();
        }

        var deleted = await dav.DeleteAsync(meeting.Href, meeting.ETag is { } etag ? DavPrecondition.Matching(etag) : DavPrecondition.None, cancellationToken)
            .ConfigureAwait(false);
        return deleted.IsSuccess || deleted.Error.Kind == ErrorKind.NotFound ? Result.Success() : deleted.Error;
    }

    /// <summary>The caller's own calendars, and the address they organize meetings as.</summary>
    private async Task<Result<(CalendarScope Scope, string Address)>> OwnScopeAsync(CancellationToken cancellationToken)
    {
        var self = await discovery.CurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (!self.IsSuccess)
        {
            return self.Error;
        }

        if (self.Value.EmailAddresses is not [var address, ..])
        {
            return Error.Forbidden("Your calendar account has no address to organize meetings as.");
        }

        var scope = await scopes.ResolveAsync(new MailboxRef(address.ToLowerInvariant()), cancellationToken).ConfigureAwait(false);
        return scope.IsSuccess ? (scope.Value, address) : scope.Error;
    }

    /// <summary>True when the stored event's link (in <c>URL</c>, or the last path segment of <c>LOCATION</c>, as Talk reads it) names the room.</summary>
    private bool IsFor(string body, string token) =>
        Primary(body) is { } vevent
        && (TalkRooms.TokenOf(options.Value, vevent.Url?.OriginalString) == token
            || (vevent.Location is { } location && TalkRooms.TokenOf(options.Value, location.Split(' ', ',', ';')[^1].Trim()) == token));

    private static bool IsRecordOf(string body, string token) =>
        Primary(body) is { } vevent
        && vevent.Attendees.Count == 0
        && vevent.Properties.FirstOrDefault(p => string.Equals(p.Name, RecordProperty, StringComparison.OrdinalIgnoreCase))?.Value?.ToString() == token;

    /// <summary>The event a stored resource is about: its single event, or a series' master.</summary>
    private static IcalEvent? Primary(string body) =>
        IcalNetCalendarCodec.Load(body) is { IsSuccess: true } loaded
            ? loaded.Value.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null) ?? loaded.Value.Events.FirstOrDefault()
            : null;

    private static XDocument LinkQuery(string property, string link) =>
        new(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                DavNs.CalDav + "calendar-query",
                new XElement(DavNames.Prop, new XElement(DavNames.GetETag), new XElement(DavNames.CalendarData)),
                new XElement(
                    DavNs.CalDav + "filter",
                    new XElement(
                        DavNs.CalDav + "comp-filter",
                        new XAttribute("name", "VCALENDAR"),
                        new XElement(
                            DavNs.CalDav + "comp-filter",
                            new XAttribute("name", "VEVENT"),
                            new XElement(
                                DavNs.CalDav + "prop-filter",
                                new XAttribute("name", property),
                                new XElement(DavNs.CalDav + "text-match", new XAttribute("collation", "i;octet"), link)))))));

    private static DateTimeTimeZone Utc(DateTimeOffset at) => new(DateTime.SpecifyKind(at.UtcDateTime, DateTimeKind.Unspecified), GraphTimeZone.Utc);
}
