using Nightware.Adapters.Dav.Calendar;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Domain.Kernel;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Calendar;

/// <summary>
/// The calendar ports over a real <see cref="DavClient"/> against WireMock, with principal discovery and
/// the collection catalog faked (as the adapter README recommends) and the real codecs. Fixtures under
/// <c>Fixtures/Calendar</c> were recorded from the dev stack (Nextcloud 35.0.3).
/// </summary>
internal sealed class CalendarAdapterHarness : IDisposable
{
    public const string Alice = "alice@nightware.localhost";
    public const string Bob = "bob@nightware.localhost";
    public const string AliceHome = "/remote.php/dav/calendars/alice/";
    public const string AlicePersonal = AliceHome + "personal/";
    public const string BobPersonal = "/remote.php/dav/calendars/bob/personal/";
    public const string SeriesHref = AlicePersonal + "series-fixture.ics";
    public const string BobSeriesHref = BobPersonal + "sabredav-63bb5443-6d4e-4891-8a9a-cb3f75a268f8.ics";
    public const string SeriesETag = "\"16546d170fda5d079a53e3460c303878\"";

    public static readonly MailboxRef AliceMailbox = new(Alice);
    public static readonly MailboxRef BobMailbox = new(Bob);

    public CalendarAdapterHarness()
    {
        Discovery.DiscoverAsync(AliceMailbox, Arg.Any<CancellationToken>()).Returns(AlicePrincipal);
        Discovery.DiscoverAsync(BobMailbox, Arg.Any<CancellationToken>()).Returns(BobPrincipal);
        Discovery.CurrentUserAsync(Arg.Any<CancellationToken>()).Returns(AlicePrincipal);
        Catalog.ListCalendarsAsync(Arg.Is<DavPrincipal>(p => p.Href == AlicePrincipal.Href), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<DavCalendar>>.Success([Personal(AlicePersonal), Birthdays()]));
        Catalog.ListCalendarsAsync(Arg.Is<DavPrincipal>(p => p.Href == BobPrincipal.Href), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<DavCalendar>>.Success([Personal(BobPersonal)]));

        Codec = new IcalNetCalendarCodec(Dav.Time);
        Editor = new IcsEditor(Dav.Time);
        Scopes = new DavCalendarScopes(Discovery, Catalog, Dav.Paths, Cache, Microsoft.Extensions.Options.Options.Create(new DavOptions()));
        Resources = new DavEventResources(Dav.Client, Scopes);
        Reader = new DavCalendarReader(Scopes, Resources, Dav.Client, Dav.Paths, Codec, Editor);
        Writer = new DavCalendarWriter(Scopes, Resources, Reader, Dav.Client, Dav.Paths, Codec, Editor, Dav.Time);
        Scheduling = new DavSchedulingActions(Resources, Reader, Editor, Discovery);
        FreeBusy = new DavFreeBusyReader(Discovery, Dav.Client, new FreeBusyCodec(Dav.Time));
    }

    public static DavPrincipal AlicePrincipal { get; } = new("/remote.php/dav/principals/users/alice/", true)
    {
        DisplayName = "Alice Anderson",
        CalendarHomeSet = AliceHome,
        ScheduleOutboxUrl = AliceHome + "outbox/",
        EmailAddresses = [Alice],
    };

    public static DavPrincipal BobPrincipal { get; } = new("/remote.php/dav/principals/users/bob/", true)
    {
        DisplayName = "Bob",
        CalendarHomeSet = "/remote.php/dav/calendars/bob/",
        EmailAddresses = [Bob],
    };

    public DavTestServer Dav { get; } = new();

    public IDavPrincipalDiscovery Discovery { get; } = Substitute.For<IDavPrincipalDiscovery>();

    public IDavCollectionCatalog Catalog { get; } = Substitute.For<IDavCollectionCatalog>();

    public Microsoft.Extensions.Caching.Memory.MemoryCache Cache { get; } = new(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

    public IcalNetCalendarCodec Codec { get; }

    public IcsEditor Editor { get; }

    public DavCalendarScopes Scopes { get; }

    public DavEventResources Resources { get; }

    public DavCalendarReader Reader { get; }

    public DavCalendarWriter Writer { get; }

    public DavSchedulingActions Scheduling { get; }

    public DavFreeBusyReader FreeBusy { get; }

    public static string Read(string name) => Fixture.Read(Path.Combine("Calendar", name));

    public static DavCalendar Personal(string href) => new(href, "Personal") { Components = new HashSet<string>(["VEVENT"]) };

    public static DavCalendar Birthdays() =>
        new(AliceHome + "contact_birthdays/", "Contact birthdays") { Components = new HashSet<string>(["VEVENT"]), IsReadOnly = true };

    /// <summary>The id the adapter mints for a stored resource.</summary>
    public EventId IdOf(string href) => DavItemId.Resource(Dav.Paths.ToRelative(href)!).ToEventId();

    public EventId OccurrenceIdOf(string href, DateTimeOffset originalStart) =>
        DavItemId.Occurrence(Dav.Paths.ToRelative(href)!, DavRecurrenceId.FromUtc(originalStart)).ToEventId();

    public void Serve(string method, string path, int status, string? body = null, string contentType = "application/xml; charset=utf-8", string? etag = null)
    {
        var response = Response.Create().WithStatusCode(status);
        if (body is not null)
        {
            response = response.WithHeader("Content-Type", contentType).WithBody(body);
        }

        if (etag is not null)
        {
            response = response.WithHeader("ETag", etag);
        }

        Dav.Server.Given(Request.Create().WithPath(path).UsingMethod(method)).RespondWith(response);
    }

    public void ServeIcs(string path, string ics, string etag = SeriesETag) =>
        Serve("GET", path, 200, ics, "text/calendar; charset=utf-8", etag);

    public IReadOnlyList<ILogEntry> Requests(string method) =>
        Dav.Server.LogEntries.Where(e => e.RequestMessage!.Method == method).ToList();

    public static string BodyOf(ILogEntry entry) => entry.RequestMessage!.Body ?? string.Empty;

    public static string? Header(ILogEntry entry, string name) =>
        entry.RequestMessage!.Headers!.TryGetValue(name, out var values) ? values[0] : null;

    public void Dispose()
    {
        Cache.Dispose();
        Dav.Dispose();
    }
}
