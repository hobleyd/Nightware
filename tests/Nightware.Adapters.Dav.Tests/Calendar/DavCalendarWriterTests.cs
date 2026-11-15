using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using static Nightware.Adapters.Dav.Tests.Calendar.CalendarAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Calendar;

public sealed class DavCalendarWriterTests : IDisposable
{
    private static readonly DateTimeOffset Moved = new(2027, 1, 10, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Third = new(2027, 1, 17, 22, 0, 0, TimeSpan.Zero);
    private readonly CalendarAdapterHarness _h = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NewEvent Meeting() =>
        new("Quarterly review", new DateTimeTimeZone(new DateTime(2027, 2, 1, 10, 0, 0), "Australia/Sydney"), new DateTimeTimeZone(new DateTime(2027, 2, 1, 11, 0, 0), "Australia/Sydney"))
        {
            Location = new Location("Boardroom") { LocationEmailAddress = "boardroom@nightware.localhost" },
            Attendees =
            [
                new Attendee(new EmailAddress(Bob), AttendeeType.Required, new ResponseStatus(ResponseType.None)),
                new Attendee(new EmailAddress("boardroom@nightware.localhost", "Boardroom"), AttendeeType.Resource, new ResponseStatus(ResponseType.None)),
            ],
        };

    [Fact]
    public async Task Create_puts_a_new_resource_in_the_default_calendar_and_rereads_it()
    {
        string? stored = null;
        _h.Dav.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(new WireMock.Matchers.WildcardMatcher(AlicePersonal + "*.ics")).UsingPut())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(201).WithCallback(r =>
            {
                stored = r.Body;
                return new WireMock.ResponseMessage { StatusCode = 201 };
            }));
        _h.Dav.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(new WireMock.Matchers.WildcardMatcher(AlicePersonal + "*.ics")).UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithCallback(_ => new WireMock.ResponseMessage
            {
                StatusCode = 200,
                BodyData = new WireMock.Util.BodyData { BodyAsString = stored!, DetectedBodyType = WireMock.Types.BodyType.String },
            }));

        var created = (await _h.Writer.CreateAsync(AliceMailbox, Meeting(), Ct)).Value;

        var put = _h.Requests("PUT").ShouldHaveSingleItem();
        Header(put, "If-None-Match").ShouldBe("*");
        put.RequestMessage!.Path.ShouldMatch(@"^/remote\.php/dav/calendars/alice/personal/[0-9a-f]{32}\.ics$");
        var ics = BodyOf(put).Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        ics.ShouldContain("ORGANIZER;CN=Alice Anderson:mailto:alice@nightware.localhost");
        ics.ShouldContain("CUTYPE=ROOM");
        ics.ShouldContain("DTSTART;TZID=Australia/Sydney:20270201T100000");
        created.Id.ShouldBe(_h.IdOf(put.RequestMessage.Path));
        created.IsOrganizer.ShouldBeTrue();
        created.Start.TimeZone.ShouldBe("Australia/Sydney");
        created.Attendees.Select(a => a.Status.Response).ShouldBe([ResponseType.NotResponded, ResponseType.NotResponded]);
    }

    [Fact]
    public async Task An_appointment_without_attendees_has_no_organizer()
    {
        _h.Serve("PUT", "/remote.php/dav/calendars/alice/personal/*", 201);
        await _h.Writer.CreateAsync(AliceMailbox, Meeting() with { Attendees = [] }, Ct);

        BodyOf(_h.Requests("PUT").ShouldHaveSingleItem()).ShouldNotContain("ORGANIZER");
    }

    [Fact]
    public async Task Create_needs_a_writable_calendar_and_reports_backend_conflicts()
    {
        _h.Catalog.ListCalendarsAsync(AlicePrincipal, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Nightware.Adapters.Dav.Core.Collections.DavCalendar>>.Success([Birthdays()]));
        (await _h.Writer.CreateAsync(AliceMailbox, Meeting(), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _h.Dav.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rejected_put_is_returned()
    {
        _h.Serve("PUT", "/remote.php/dav/calendars/alice/personal/*", 507);
        (await _h.Writer.CreateAsync(AliceMailbox, Meeting(), Ct)).Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
    }

    [Fact]
    public async Task Update_edits_the_stored_resource_under_its_etag_and_reinvites_on_reschedule()
    {
        _h.ServeIcs(SeriesHref, Read("series-alice.ics"));
        _h.Serve("PUT", SeriesHref, 204);
        var patch = new EventPatch
        {
            Start = new DateTimeTimeZone(new DateTime(2027, 1, 4, 10, 0, 0), "Australia/Sydney"),
            End = new DateTimeTimeZone(new DateTime(2027, 1, 4, 10, 30, 0), "Australia/Sydney"),
        };

        (await _h.Writer.UpdateAsync(AliceMailbox, _h.IdOf(SeriesHref), patch, Ct)).IsSuccess.ShouldBeTrue();

        var put = _h.Requests("PUT").ShouldHaveSingleItem();
        Header(put, "If-Match").ShouldBe(SeriesETag);
        var body = BodyOf(put).Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        body.ShouldContain("DTSTART;TZID=Australia/Sydney:20270104T100000");
        body.ShouldContain("SEQUENCE:1");
        body.ShouldContain("SCHEDULE-STATUS=1.1"); // what Sabre stored survives the edit
    }

    [Fact]
    public async Task A_stale_etag_is_a_precondition_failure()
    {
        _h.ServeIcs(SeriesHref, Read("series-alice.ics"));
        _h.Serve("PUT", SeriesHref, 412);
        (await _h.Writer.UpdateAsync(AliceMailbox, _h.IdOf(SeriesHref), new EventPatch { Subject = "x" }, Ct)).Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
    }

    [Fact]
    public async Task Updating_an_occurrence_writes_its_exception()
    {
        _h.ServeIcs(SeriesHref, Read("series-alice.ics"));
        _h.Serve("PUT", SeriesHref, 204);

        await _h.Writer.UpdateAsync(AliceMailbox, _h.OccurrenceIdOf(SeriesHref, Third), new EventPatch { Subject = "Third, renamed" }, Ct);

        var body = BodyOf(_h.Requests("PUT").ShouldHaveSingleItem());
        body.ShouldContain("RECURRENCE-ID;TZID=Australia/Sydney:20270118T090000");
        var events = _h.Codec.Parse(body).Value.Events;
        events.Single(e => e.Type == EventType.Exception && e.OriginalStart == Third).Subject.ShouldBe("Third, renamed");
        events.Single(e => e.Type == EventType.SeriesMaster).Subject.ShouldBe("Fixture weekly");
    }

    [Fact]
    public async Task Delete_removes_the_resource()
    {
        _h.Serve("DELETE", SeriesHref, 204);
        (await _h.Writer.DeleteAsync(AliceMailbox, _h.IdOf(SeriesHref), Ct)).IsSuccess.ShouldBeTrue();
        _h.Requests("DELETE").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Deleting_an_occurrence_excludes_it()
    {
        _h.ServeIcs(SeriesHref, Read("series-alice.ics"));
        _h.Serve("PUT", SeriesHref, 204);

        (await _h.Writer.DeleteAsync(AliceMailbox, _h.OccurrenceIdOf(SeriesHref, Moved), Ct)).IsSuccess.ShouldBeTrue();

        var body = BodyOf(_h.Requests("PUT").ShouldHaveSingleItem());
        body.ShouldContain("EXDATE;TZID=Australia/Sydney:20270111T090000");
        body.ShouldNotContain("(moved)");
        _h.Requests("DELETE").ShouldBeEmpty();
    }

    [Fact]
    public async Task Writes_outside_the_mailbox_are_not_found()
    {
        (await _h.Writer.DeleteAsync(AliceMailbox, _h.IdOf(BobSeriesHref), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _h.Writer.UpdateAsync(AliceMailbox, _h.IdOf(BobSeriesHref), new EventPatch(), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _h.Dav.Server.LogEntries.ShouldBeEmpty();
    }

    public void Dispose() => _h.Dispose();
}
