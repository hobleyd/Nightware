using System.Xml.Linq;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Calendar;
using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using static Nightware.Adapters.Dav.Tests.Calendar.CalendarAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Calendar;

/// <summary>calendarView over Sabre's expansion, single-event reads and the iCalUId lookup, on recorded responses.</summary>
public sealed class DavCalendarReaderTests : IDisposable
{
    // The recorded weekly series: Mondays 09:00 Sydney from 2027-01-04, four times, the 11th moved to 11:00.
    private static readonly DateTimeOffset First = new(2027, 1, 3, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Moved = new(2027, 1, 10, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Window = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly CalendarAdapterHarness _h = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void ServeView()
    {
        _h.Dav.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(AlicePersonal).UsingMethod("REPORT")
                .WithBody(new WireMock.Matchers.WildcardMatcher("*calendar-query*")))
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(207).WithBody(Read("series-expanded.xml")));
        _h.Dav.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(AlicePersonal).UsingMethod("REPORT")
                .WithBody(new WireMock.Matchers.WildcardMatcher("*calendar-multiget*")))
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(207).WithBody(Read("series-multiget.xml")));
    }

    [Fact]
    public async Task Calendar_view_expands_on_the_server_and_tells_exceptions_from_occurrences()
    {
        ServeView();

        var page = (await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddMonths(1), new ItemQuery { Top = 100 }, Ct)).Value;

        page.Items.Select(e => e.Type).ShouldBe([EventType.Occurrence, EventType.Exception, EventType.Occurrence, EventType.Occurrence]);
        page.HasMore.ShouldBeFalse();
        var moved = page.Items[1];
        moved.Subject.ShouldBe("Fixture weekly (moved)");
        moved.Id.ShouldBe(_h.OccurrenceIdOf(SeriesHref, Moved));
        moved.SeriesMasterId.ShouldBe(_h.IdOf(SeriesHref));
        moved.Start.ShouldBe(DateTimeTimeZone.Utc(new DateTimeOffset(2027, 1, 11, 0, 0, 0, TimeSpan.Zero)));
        page.Items[0].Id.ShouldBe(_h.OccurrenceIdOf(SeriesHref, First));
        page.Items.ShouldAllBe(e => e.Recurrence == null && e.IsOrganizer);

        var report = XDocument.Parse(BodyOf(_h.Requests("REPORT")[0]));
        var expand = report.Descendants(DavNs.CalDav + "expand").ShouldHaveSingleItem();
        expand.Attribute("start")!.Value.ShouldBe("20270101T000000Z");
        expand.Attribute("end")!.Value.ShouldBe("20270201T000000Z");
        report.Descendants(DavNs.CalDav + "time-range").ShouldHaveSingleItem();
        Header(_h.Requests("REPORT")[0], "Depth").ShouldBe("1");
        var multiget = XDocument.Parse(BodyOf(_h.Requests("REPORT")[1]));
        multiget.Descendants(DavNames.Href).Select(h => h.Value).ShouldBe([SeriesHref]);
    }

    [Fact]
    public async Task Calendar_view_pages_by_top_with_an_offset_continuation()
    {
        ServeView();

        var first = (await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddMonths(1), new ItemQuery { Top = 3 }, Ct)).Value;
        var rest = (await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddMonths(1), new ItemQuery { Top = 3, SkipToken = first.SkipToken }, Ct)).Value;

        first.Items.Count.ShouldBe(3);
        first.SkipToken.ShouldBe("3");
        rest.Items.ShouldHaveSingleItem().Start.DateTime.ShouldBe(new DateTime(2027, 1, 24, 22, 0, 0));
        rest.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task A_follow_up_page_reads_the_first_pages_expansion_and_a_new_walk_reads_afresh()
    {
        ServeView();
        int Expansions() => _h.Requests("REPORT").Count(r => BodyOf(r).Contains("calendar-query", StringComparison.Ordinal));

        var first = (await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddMonths(1), new ItemQuery { Top = 3 }, Ct)).Value;
        await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddMonths(1), new ItemQuery { Top = 3, SkipToken = first.SkipToken }, Ct);
        Expansions().ShouldBe(1);

        await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddMonths(1), new ItemQuery { Top = 3 }, Ct);
        Expansions().ShouldBe(2);

        // A single page (no continuation) isn't kept: a later page of that window expands again.
        await _h.Reader.CalendarViewAsync(AliceMailbox, Window.AddDays(1), Window.AddMonths(1), new ItemQuery { Top = 100 }, Ct);
        await _h.Reader.CalendarViewAsync(AliceMailbox, Window.AddDays(1), Window.AddMonths(1), new ItemQuery { Top = 100, SkipToken = "1" }, Ct);
        Expansions().ShouldBe(4);
        var before = Expansions();

        // A minute on, the kept expansion is gone and the page expands again.
        _h.Dav.Time.Advance(Nightware.Adapters.Dav.Calendar.CalendarViewSnapshots.Lifetime + TimeSpan.FromSeconds(1));
        await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddMonths(1), new ItemQuery { Top = 3, SkipToken = first.SkipToken }, Ct);
        Expansions().ShouldBe(before + 1);
    }

    [Fact]
    public async Task Calendar_view_without_recurring_events_needs_no_multiget()
    {
        _h.Serve("REPORT", AlicePersonal, 207, Read("../Recorded/03-calendar-query-timerange.response.xml"));

        var page = (await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddDays(7), ItemQuery.Default, Ct)).Value;

        page.Items.Count.ShouldBe(4);
        page.Items.ShouldAllBe(e => e.Type == EventType.SingleInstance);
        _h.Requests("REPORT").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Calendar_view_lists_the_calendar_home_once_then_remembers_the_default_calendar()
    {
        _h.Serve("REPORT", AlicePersonal, 207, Read("../Recorded/03-calendar-query-timerange.response.xml"));

        await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddDays(7), ItemQuery.Default, Ct);
        var again = await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddDays(7), ItemQuery.Default, Ct);

        again.Value.Items.Count.ShouldBe(4);
        await _h.Catalog.Received(1).ListCalendarsAsync(Arg.Any<Nightware.Adapters.Dav.Core.Discovery.DavPrincipal>(), Arg.Any<CancellationToken>());
        _h.Requests("REPORT").Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_remembered_default_calendar_that_is_gone_is_looked_up_again()
    {
        await _h.Scopes.DefaultAsync(AliceMailbox, Ct);
        _h.Catalog.ListCalendarsAsync(Arg.Is<Nightware.Adapters.Dav.Core.Discovery.DavPrincipal>(p => p.Href == AlicePrincipal.Href), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<Nightware.Adapters.Dav.Core.Collections.DavCalendar>>.Success([Personal(AliceHome + "work/")]));
        _h.Serve("REPORT", AlicePersonal, 404, "");
        _h.Serve("REPORT", AliceHome + "work/", 207, Read("../Recorded/03-calendar-query-timerange.response.xml"));

        var page = await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddDays(7), ItemQuery.Default, Ct);

        page.Value.Items.Count.ShouldBe(4);
        (await _h.Scopes.DefaultAsync(AliceMailbox, Ct)).Value!.Href.ShouldBe(AliceHome + "work/");
    }

    [Fact]
    public async Task A_shared_calendars_default_is_remembered_per_caller()
    {
        var bob = BobPrincipal with { IsCurrentUser = false };
        _h.Discovery.DiscoverAsync(BobMailbox, Arg.Any<CancellationToken>()).Returns(bob);
        var sharedWithAlice = Personal(AliceHome + "personal_shared_by_bob/") with { IsShared = true, OwnerPrincipal = "principals/users/bob" };
        _h.Catalog.ListCalendarsAsync(Arg.Any<Nightware.Adapters.Dav.Core.Discovery.DavPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<Nightware.Adapters.Dav.Core.Collections.DavCalendar>>.Success([sharedWithAlice]));

        (await _h.Scopes.DefaultAsync(BobMailbox, Ct)).Value!.Href.ShouldBe(sharedWithAlice.Href);
        (await _h.Scopes.DefaultAsync(BobMailbox, Ct)).IsSuccess.ShouldBeTrue();
        await _h.Catalog.Received(1).ListCalendarsAsync(Arg.Any<Nightware.Adapters.Dav.Core.Discovery.DavPrincipal>(), Arg.Any<CancellationToken>());

        _h.Discovery.CurrentUserAsync(Arg.Any<CancellationToken>()).Returns(AlicePrincipal with { Href = "/remote.php/dav/principals/users/carol/" });
        await _h.Scopes.DefaultAsync(BobMailbox, Ct);

        await _h.Catalog.Received(2).ListCalendarsAsync(Arg.Any<Nightware.Adapters.Dav.Core.Discovery.DavPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Calendar_view_refuses_a_window_over_the_cap_before_asking_the_server()
    {
        ServeView();

        // An invitation's own DTSTART/DTEND can set the window (messages/{id}/event), so the reader bounds it too.
        var result = await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window + CalendarViewLimits.MaxWindow + TimeSpan.FromTicks(1), ItemQuery.Default, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        _h.Dav.Server.LogEntries.ShouldBeEmpty();
        (await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window + CalendarViewLimits.MaxWindow, ItemQuery.Default, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Calendar_view_rejects_filters_and_passes_backend_errors_through()
    {
        var filtered = new ItemQuery { Filter = new FilterNode.Compare("subject", CompareOp.Eq, new FilterValue.Text("x")) };
        (await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddDays(1), filtered, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _h.Dav.Server.LogEntries.ShouldBeEmpty();

        _h.Serve("REPORT", AlicePersonal, 503);
        (await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddDays(1), ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task A_mailbox_without_a_writable_calendar_has_an_empty_view()
    {
        _h.Catalog.ListCalendarsAsync(AlicePrincipal, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Nightware.Adapters.Dav.Core.Collections.DavCalendar>>.Success([Birthdays()]));

        (await _h.Reader.CalendarViewAsync(AliceMailbox, Window, Window.AddDays(1), ItemQuery.Default, Ct)).Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_resource_id_reads_the_series_master()
    {
        _h.ServeIcs(SeriesHref, Read("series-alice.ics"));

        var master = (await _h.Reader.GetAsync(AliceMailbox, _h.IdOf(SeriesHref), Ct)).Value;

        master.Type.ShouldBe(EventType.SeriesMaster);
        master.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2027, 1, 4, 9, 0, 0), "Australia/Sydney"));
        master.Recurrence!.Range.NumberOfOccurrences.ShouldBe(4);
        master.Attendees.ShouldHaveSingleItem().EmailAddress.Address.ShouldBe(Bob);
    }

    [Fact]
    public async Task An_unmodified_occurrence_is_read_from_its_master()
    {
        _h.ServeIcs(SeriesHref, Read("series-alice.ics"));
        var third = new DateTimeOffset(2027, 1, 17, 22, 0, 0, TimeSpan.Zero);

        var occurrence = (await _h.Reader.GetAsync(AliceMailbox, _h.OccurrenceIdOf(SeriesHref, third), Ct)).Value;

        occurrence.Type.ShouldBe(EventType.Occurrence);
        occurrence.Id.ShouldBe(_h.OccurrenceIdOf(SeriesHref, third));
        occurrence.SeriesMasterId.ShouldBe(_h.IdOf(SeriesHref));
        occurrence.OriginalStart.ShouldBe(third);
        occurrence.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2027, 1, 18, 9, 0, 0), "Australia/Sydney"));
        occurrence.End.ShouldBe(new DateTimeTimeZone(new DateTime(2027, 1, 18, 9, 30, 0), "Australia/Sydney"));
        occurrence.Recurrence.ShouldBeNull();
    }

    [Fact]
    public async Task A_modified_occurrence_is_its_exception()
    {
        _h.ServeIcs(SeriesHref, Read("series-alice.ics"));

        var moved = (await _h.Reader.GetAsync(AliceMailbox, _h.OccurrenceIdOf(SeriesHref, Moved), Ct)).Value;

        moved.Type.ShouldBe(EventType.Exception);
        moved.Subject.ShouldBe("Fixture weekly (moved)");
    }

    [Fact]
    public async Task Occurrences_the_series_does_not_have_are_not_found()
    {
        _h.ServeIcs(SeriesHref, Read("series-alice.ics").Replace("COUNT=4", "COUNT=4\r\nEXDATE;TZID=Australia/Sydney:20270118T090000", StringComparison.Ordinal));

        (await _h.Reader.GetAsync(AliceMailbox, _h.OccurrenceIdOf(SeriesHref, new DateTimeOffset(2027, 1, 17, 22, 0, 0, TimeSpan.Zero)), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _h.Reader.GetAsync(AliceMailbox, _h.OccurrenceIdOf(SeriesHref, new DateTimeOffset(2027, 1, 31, 22, 0, 0, TimeSpan.Zero)), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Ids_outside_the_mailbox_or_tampered_are_not_found_without_a_request()
    {
        (await _h.Reader.GetAsync(AliceMailbox, _h.IdOf(BobSeriesHref), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _h.Reader.GetAsync(AliceMailbox, new EventId("not-an-id!"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _h.Dav.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_deleted_resource_is_not_found()
    {
        _h.Serve("GET", SeriesHref, 404);
        (await _h.Reader.GetAsync(AliceMailbox, _h.IdOf(SeriesHref), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task The_icaluid_lookup_searches_every_calendar_and_returns_masters_only()
    {
        _h.Serve("REPORT", AlicePersonal, 207, Read("series-multiget.xml"));
        _h.Serve("REPORT", AliceHome + "contact_birthdays/", 207, "<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\"/>");
        var query = new ItemQuery { Filter = new FilterNode.Compare("iCalUId", CompareOp.Eq, new FilterValue.Text("series-fixture")), Top = 5 };

        var page = (await _h.Reader.ListAsync(AliceMailbox, query, Ct)).Value;

        var master = page.Items.ShouldHaveSingleItem();
        master.Type.ShouldBe(EventType.SeriesMaster);
        master.Id.ShouldBe(_h.IdOf(SeriesHref));
        var reports = _h.Requests("REPORT");
        reports.Count.ShouldBe(2);
        var match = XDocument.Parse(BodyOf(reports[0])).Descendants(DavNs.CalDav + "text-match").ShouldHaveSingleItem();
        match.Value.ShouldBe("series-fixture");
        match.Attribute("collation")!.Value.ShouldBe("i;octet");
    }

    [Fact]
    public async Task An_attendee_invited_to_one_occurrence_only_finds_that_exception()
    {
        var onlyException = Read("series-multiget.xml");
        var masterStart = onlyException.IndexOf("BEGIN:VEVENT", StringComparison.Ordinal);
        var masterEnd = onlyException.IndexOf("END:VEVENT", masterStart, StringComparison.Ordinal) + "END:VEVENT".Length;
        _h.Serve("REPORT", AlicePersonal, 207, onlyException.Remove(masterStart, masterEnd - masterStart));
        _h.Serve("REPORT", AliceHome + "contact_birthdays/", 207, "<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\"/>");
        var query = new ItemQuery { Filter = new FilterNode.Compare("iCalUId", CompareOp.Eq, new FilterValue.Text("series-fixture")) };

        var found = (await _h.Reader.ListAsync(AliceMailbox, query, Ct)).Value.Items.ShouldHaveSingleItem();

        found.Type.ShouldBe(EventType.Exception);
        found.OriginalStart.ShouldBe(Moved);
    }

    [Fact]
    public async Task An_unfiltered_listing_reads_the_default_calendar()
    {
        _h.Serve("REPORT", AlicePersonal, 207, Read("series-multiget.xml"));

        (await _h.Reader.ListAsync(AliceMailbox, ItemQuery.Default, Ct)).Value.Items.ShouldHaveSingleItem();

        var report = XDocument.Parse(BodyOf(_h.Requests("REPORT").ShouldHaveSingleItem()));
        report.Descendants(DavNs.CalDav + "prop-filter").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("iCalUId ne")]
    public async Task Other_filters_are_rejected_for_the_clients_fallback(string shape)
    {
        FilterNode filter = shape == "subject"
            ? new FilterNode.Compare("subject", CompareOp.Eq, new FilterValue.Text("x"))
            : new FilterNode.Compare("iCalUId", CompareOp.Ne, new FilterValue.Text("x"));

        (await _h.Reader.ListAsync(AliceMailbox, new ItemQuery { Filter = filter }, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _h.Dav.Server.LogEntries.ShouldBeEmpty();
    }

    public void Dispose() => _h.Dispose();
}
