using Nightware.Adapters.Dav.Calendar;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Application.Changes;
using Nightware.Domain.Kernel;
using static Nightware.Adapters.Dav.Tests.Calendar.CalendarAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Calendar;

public sealed class DavCalendarChangeProbeTests : IDisposable
{
    private readonly CalendarAdapterHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    private DavCalendarChangeProbe Probe => new(_h.Scopes);

    private void Calendars(params DavCalendar[] calendars) =>
        _h.Catalog.ListCalendarsAsync(Arg.Is<DavPrincipal>(p => p.Href == AlicePrincipal.Href), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<DavCalendar>>.Success(calendars));

    [Fact]
    public async Task The_state_names_each_event_calendar_with_its_sync_token_in_href_order()
    {
        Calendars(
            Personal(AlicePersonal) with { SyncToken = "sync/7" },
            Birthdays() with { CTag = "ctag-3" },
            Personal(AliceHome + "team/") with { SyncToken = "sync/2", IsShared = true });

        var state = (await Probe.StateAsync(AliceMailbox, Ct)).Value;

        state.ShouldBe($"{AliceHome}contact_birthdays/=ctag-3;{AlicePersonal}=sync/7;{AliceHome}team/=sync/2;");
        Probe.Area.ShouldBe(ChangeArea.Calendar);
    }

    [Fact]
    public async Task An_event_changed_in_any_calendar_moves_the_state()
    {
        Calendars(Personal(AlicePersonal) with { SyncToken = "sync/7" });
        var before = (await Probe.StateAsync(AliceMailbox, Ct)).Value;
        Calendars(Personal(AlicePersonal) with { SyncToken = "sync/8" });

        (await Probe.StateAsync(AliceMailbox, Ct)).Value.ShouldNotBe(before);
    }

    [Fact]
    public async Task A_mailbox_with_no_calendar_the_caller_can_see_is_forbidden_which_the_watcher_drops()
    {
        _h.Discovery.DiscoverAsync(new MailboxRef("nobody@nightware.localhost"), Arg.Any<CancellationToken>())
            .Returns(Error.NotFound("no principal"));

        (await Probe.StateAsync(new MailboxRef("nobody@nightware.localhost"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }
}
