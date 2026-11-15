using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using static Nightware.Adapters.Dav.Tests.Calendar.CalendarAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Calendar;

public sealed class DavFreeBusyReaderTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
    private readonly CalendarAdapterHarness _h = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_request_to_the_callers_outbox_answers_for_people_and_rooms()
    {
        _h.Serve("POST", AliceHome + "outbox/", 200, Fixture.Read(Path.Combine("Recorded", "09-freebusy-post.response.xml")));

        var schedules = (await _h.FreeBusy.GetAsync(new MailboxRef("shared@nightware.localhost"), ["boardroom@nightware.localhost", Bob], Day, Day.AddDays(1), Ct)).Value;

        schedules.Select(s => s.ScheduleId).ShouldBe(["boardroom@nightware.localhost", Bob]);
        schedules.ShouldAllBe(s => s.ErrorMessage == null);
        var busy = schedules[1].Items.ShouldHaveSingleItem();
        busy.Start.ShouldBe(Day.AddHours(10));
        busy.Status.ShouldBe(FreeBusyStatus.Busy);
        var post = _h.Requests("POST").ShouldHaveSingleItem();
        Header(post, "Content-Type").ShouldStartWith("text/calendar");
        var ics = BodyOf(post);
        ics.ShouldContain("ORGANIZER:mailto:alice@nightware.localhost"); // the caller, even for another mailbox
        ics.ShouldContain("ATTENDEE:mailto:boardroom@nightware.localhost");
        ics.ShouldContain("DTSTART:20261115T000000Z");
    }

    [Fact]
    public async Task Unknown_recipients_carry_an_error()
    {
        _h.Serve("POST", AliceHome + "outbox/", 200,
            "<?xml version=\"1.0\"?><cal:schedule-response xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><cal:response><cal:recipient><d:href>mailto:who%40example.com</d:href></cal:recipient><cal:request-status>3.7;Could not find principal</cal:request-status></cal:response></cal:schedule-response>");

        var schedule = (await _h.FreeBusy.GetAsync(AliceMailbox, ["who@example.com"], Day, Day.AddDays(1), Ct)).Value.ShouldHaveSingleItem();

        schedule.ScheduleId.ShouldBe("who@example.com");
        schedule.ErrorMessage.ShouldNotBeNull().ShouldContain("3.7");
    }

    [Fact]
    public async Task No_outbox_is_forbidden_and_errors_pass_through()
    {
        _h.Discovery.CurrentUserAsync(Arg.Any<CancellationToken>()).Returns(new DavPrincipal("/remote.php/dav/principals/users/alice/", true));
        (await _h.FreeBusy.GetAsync(AliceMailbox, [Bob], Day, Day.AddDays(1), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        _h.Discovery.CurrentUserAsync(Arg.Any<CancellationToken>()).Returns(AlicePrincipal);
        _h.Serve("POST", AliceHome + "outbox/", 403);
        (await _h.FreeBusy.GetAsync(AliceMailbox, [Bob], Day, Day.AddDays(1), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task No_addresses_need_no_request() =>
        (await _h.FreeBusy.GetAsync(AliceMailbox, [], Day, Day.AddDays(1), Ct)).Value.ShouldBeEmpty();

    public void Dispose() => _h.Dispose();
}
