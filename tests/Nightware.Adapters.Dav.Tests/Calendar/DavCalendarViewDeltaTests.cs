using Nightware.Adapters.Dav.Calendar;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Domain.Kernel;
using static Nightware.Adapters.Dav.Tests.Calendar.CalendarAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Calendar;

public sealed class DavCalendarViewDeltaTests : IDisposable
{
    private static readonly DateTimeOffset Window = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly CalendarAdapterHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DavCalendarViewDeltaTests()
    {
        _h.Catalog.ListCalendarsAsync(Arg.Is<Nightware.Adapters.Dav.Core.Discovery.DavPrincipal>(p => p.Href == AlicePrincipal.Href), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<DavCalendar>>.Success([Personal(AlicePersonal) with { SyncToken = "http://sabre.io/ns/sync/7" }]));
        _h.Dav.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(AlicePersonal).UsingMethod("REPORT")
                .WithBody(new WireMock.Matchers.WildcardMatcher("*calendar-query*")))
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(207).WithBody(Read("series-expanded.xml")));
        _h.Dav.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(AlicePersonal).UsingMethod("REPORT")
                .WithBody(new WireMock.Matchers.WildcardMatcher("*calendar-multiget*")))
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(207).WithBody(Read("series-multiget.xml")));
    }

    public void Dispose() => _h.Dispose();

    private DavCalendarViewDelta Delta() => new(_h.Scopes, _h.Reader, _h.Dav.Client, _h.Dav.Paths);

    [Fact]
    public async Task The_initial_sync_is_the_windows_occurrences_and_a_token_from_the_catalogs_sync_token()
    {
        var page = (await Delta().DeltaAsync(AliceMailbox, Window, Window.AddMonths(1), null, Ct)).Value;

        page.Entries.Count.ShouldBe(4);
        page.Final.ShouldNotBeNull();
        _h.Requests("REPORT").ShouldNotContain(r => BodyOf(r).Contains("sync-collection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changes_upsert_a_changed_series_occurrences_and_remove_a_deleted_resource()
    {
        var initial = (await Delta().DeltaAsync(AliceMailbox, Window, Window.AddMonths(1), null, Ct)).Value;
        _h.Dav.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(AlicePersonal).UsingMethod("REPORT")
                .WithBody(new WireMock.Matchers.WildcardMatcher("*sync-collection*")))
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(207).WithBody($$"""
                <?xml version="1.0"?><d:multistatus xmlns:d="DAV:">
                  <d:response><d:href>{{SeriesHref}}</d:href><d:propstat><d:prop><d:getetag>"e2"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                  <d:response><d:href>{{AlicePersonal}}gone.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>
                  <d:sync-token>http://sabre.io/ns/sync/9</d:sync-token>
                </d:multistatus>
                """));

        var changes = (await Delta().DeltaAsync(AliceMailbox, null, null, initial.Final, Ct)).Value;

        changes.Entries.OfType<Nightware.Domain.Kernel.DeltaEntry<Nightware.Domain.Calendar.CalendarEvent>.Upsert>().Count().ShouldBe(4);
        changes.Entries.OfType<Nightware.Domain.Kernel.DeltaEntry<Nightware.Domain.Calendar.CalendarEvent>.Removed>().ShouldHaveSingleItem()
            .Id.ShouldBe(_h.IdOf(AlicePersonal + "gone.ics").Value);
    }

    [Fact]
    public async Task A_token_that_is_not_ours_is_410()
    {
        (await Delta().DeltaAsync(AliceMailbox, null, null, new DeltaToken("not-a-token"), Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }
}
