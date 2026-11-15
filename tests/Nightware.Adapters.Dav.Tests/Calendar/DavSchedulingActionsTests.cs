using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using static Nightware.Adapters.Dav.Tests.Calendar.CalendarAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Calendar;

/// <summary>RSVP, cancel and forward edit the stored copy; Sabre's scheduling turns the PUT/DELETE into iTIP.</summary>
public sealed class DavSchedulingActionsTests : IDisposable
{
    private static readonly DateTimeOffset Third = new(2027, 1, 17, 22, 0, 0, TimeSpan.Zero);
    private readonly CalendarAdapterHarness _h = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Body() => BodyOf(_h.Requests("PUT").ShouldHaveSingleItem()).Replace("\r\n ", string.Empty, StringComparison.Ordinal);

    [Fact]
    public async Task An_attendee_accepts_the_series_on_their_copy()
    {
        _h.ServeIcs(BobSeriesHref, Read("series-bob.ics"), "\"bob-etag\"");
        _h.Serve("PUT", BobSeriesHref, 204);

        (await _h.Scheduling.RespondAsync(BobMailbox, _h.IdOf(BobSeriesHref), RsvpResponse.Accept, "See you", true, null, Ct)).IsSuccess.ShouldBeTrue();

        Header(_h.Requests("PUT")[0], "If-Match").ShouldBe("\"bob-etag\"");
        var body = Body();
        body.Split("PARTSTAT=ACCEPTED").Length.ShouldBe(3); // master and the moved exception
        body.ShouldNotContain("RSVP=TRUE");
        body.ShouldContain("COMMENT:See you");
    }

    [Fact]
    public async Task An_attendee_declines_one_unmodified_occurrence()
    {
        _h.ServeIcs(BobSeriesHref, Read("series-bob.ics"));
        _h.Serve("PUT", BobSeriesHref, 204);

        await _h.Scheduling.RespondAsync(BobMailbox, _h.OccurrenceIdOf(BobSeriesHref, Third), RsvpResponse.Decline, null, true, null, Ct);

        var body = Body();
        body.ShouldContain("RECURRENCE-ID;TZID=Australia/Sydney:20270118T090000");
        body.Split("PARTSTAT=DECLINED").Length.ShouldBe(2);
        body.Split("PARTSTAT=NEEDS-ACTION").Length.ShouldBe(3);
    }

    [Fact]
    public async Task The_organizer_cannot_respond()
    {
        _h.ServeIcs(SeriesHref, Read("series-alice.ics"));
        (await _h.Scheduling.RespondAsync(AliceMailbox, _h.IdOf(SeriesHref), RsvpResponse.Accept, null, true, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _h.Requests("PUT").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_organizer_cancels_by_deleting_so_sabre_sends_cancel()
    {
        _h.ServeIcs(SeriesHref, Read("series-alice.ics"));
        _h.Serve("DELETE", SeriesHref, 204);

        (await _h.Scheduling.CancelAsync(AliceMailbox, _h.IdOf(SeriesHref), "Sorry", Ct)).IsSuccess.ShouldBeTrue();

        Header(_h.Requests("DELETE").ShouldHaveSingleItem(), "If-Match").ShouldBe(SeriesETag);
    }

    [Fact]
    public async Task Cancelling_one_occurrence_excludes_it()
    {
        _h.ServeIcs(SeriesHref, Read("series-alice.ics"));
        _h.Serve("PUT", SeriesHref, 204);

        await _h.Scheduling.CancelAsync(AliceMailbox, _h.OccurrenceIdOf(SeriesHref, Third), null, Ct);

        Body().ShouldContain("EXDATE;TZID=Australia/Sydney:20270118T090000");
        _h.Requests("DELETE").ShouldBeEmpty();
    }

    [Fact]
    public async Task Attendees_can_neither_cancel_nor_forward()
    {
        _h.ServeIcs(BobSeriesHref, Read("series-bob.ics"));

        (await _h.Scheduling.CancelAsync(BobMailbox, _h.IdOf(BobSeriesHref), null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await _h.Scheduling.ForwardAsync(BobMailbox, _h.IdOf(BobSeriesHref), [new EmailAddress("pat@nightware.localhost")], null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _h.Requests("PUT").ShouldBeEmpty();
        _h.Requests("DELETE").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_organizer_forwards_by_inviting_the_recipients()
    {
        _h.ServeIcs(SeriesHref, Read("series-alice.ics"));
        _h.Serve("PUT", SeriesHref, 204);

        (await _h.Scheduling.ForwardAsync(AliceMailbox, _h.IdOf(SeriesHref), [new EmailAddress("pat@nightware.localhost")], "Join us", Ct)).IsSuccess.ShouldBeTrue();

        Body().Split("mailto:pat@nightware.localhost").Length.ShouldBe(3);
    }

    [Fact]
    public async Task Failures_reading_the_event_pass_through()
    {
        _h.Serve("GET", SeriesHref, 404);
        (await _h.Scheduling.CancelAsync(AliceMailbox, _h.IdOf(SeriesHref), null, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _h.Scheduling.RespondAsync(AliceMailbox, new EventId("bogus"), RsvpResponse.Accept, null, true, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    public void Dispose() => _h.Dispose();
}
