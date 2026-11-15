using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Domain.Kernel;
using static Nightware.Adapters.Dav.Tests.Calendar.CalendarAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Calendar;

public sealed class DavCalendarScopesTests : IDisposable
{
    private readonly CalendarAdapterHarness _h = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_callers_own_calendars_default_to_personal()
    {
        var scope = (await _h.Scopes.ResolveAsync(AliceMailbox, Ct)).Value;

        scope.Calendars.Select(c => c.Href).ShouldBe([AlicePersonal, AliceHome + "contact_birthdays/"]);
        scope.Default!.Href.ShouldBe(AlicePersonal);
        scope.Relative.ShouldBe(["calendars/alice/personal/", "calendars/alice/contact_birthdays/"]);
    }

    [Fact]
    public async Task Read_only_birthdays_and_shared_calendars_are_never_the_default()
    {
        _h.Catalog.ListCalendarsAsync(AlicePrincipal, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DavCalendar>>.Success(
        [
            Birthdays(),
            new DavCalendar(AliceHome + "work/", "Work") { Order = 2 },
            new DavCalendar(AliceHome + "home/", "Home") { Order = 1 },
            new DavCalendar(AliceHome + "bobs_shared_by_bob/", "Bob's") { IsShared = true, OwnerPrincipal = "principals/users/bob" },
            new DavCalendar(AliceHome + "tasks/", "Tasks") { Components = new HashSet<string>(["VTODO"]) },
        ]));

        var scope = (await _h.Scopes.ResolveAsync(AliceMailbox, Ct)).Value;

        scope.Default!.Href.ShouldBe(AliceHome + "home/");
        scope.Calendars.Select(c => c.DisplayName).ShouldBe(["Contact birthdays", "Work", "Home"]);
    }

    [Fact]
    public async Task Another_mailbox_is_the_calendars_its_owner_shared_with_the_caller()
    {
        var carol = new DavPrincipal("/remote.php/dav/principals/users/carol/", false) { EmailAddresses = ["carol@nightware.localhost"] };
        _h.Discovery.DiscoverAsync(new MailboxRef("carol@nightware.localhost"), Arg.Any<CancellationToken>()).Returns(carol);
        _h.Catalog.ListCalendarsAsync(AlicePrincipal, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DavCalendar>>.Success(
        [
            Personal(AlicePersonal),
            new DavCalendar(AliceHome + "personal_shared_by_carol/", "Carol") { IsShared = true, OwnerPrincipal = "principals/users/carol" },
            new DavCalendar(AliceHome + "personal_shared_by_dave/", "Dave") { IsShared = true, OwnerPrincipal = "principals/users/dave" },
        ]));

        var scope = (await _h.Scopes.ResolveAsync(new MailboxRef("carol@nightware.localhost"), Ct)).Value;

        scope.Calendars.ShouldHaveSingleItem().Href.ShouldBe(AliceHome + "personal_shared_by_carol/");
        scope.Default!.Href.ShouldBe(AliceHome + "personal_shared_by_carol/");
        await _h.Catalog.DidNotReceive().ListCalendarsAsync(carol, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_mailbox_that_shares_nothing_is_forbidden()
    {
        var carol = new DavPrincipal("/remote.php/dav/principals/users/carol/", false);
        _h.Discovery.DiscoverAsync(new MailboxRef("carol@nightware.localhost"), Arg.Any<CancellationToken>()).Returns(carol);

        (await _h.Scopes.ResolveAsync(new MailboxRef("carol@nightware.localhost"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task An_unknown_mailbox_is_forbidden_and_backend_failures_pass_through()
    {
        _h.Discovery.DiscoverAsync(new MailboxRef("nobody@nightware.localhost"), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no principal"));
        (await _h.Scopes.ResolveAsync(new MailboxRef("nobody@nightware.localhost"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        _h.Catalog.ListCalendarsAsync(AlicePrincipal, Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));
        (await _h.Scopes.ResolveAsync(AliceMailbox, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Ids_are_in_scope_only_inside_the_mailboxs_calendars()
    {
        var scope = (await _h.Scopes.ResolveAsync(AliceMailbox, Ct)).Value;

        scope.Contains(Nightware.Adapters.Dav.Core.DavItemId.Resource("calendars/alice/personal/x.ics")).ShouldBeTrue();
        scope.Contains(Nightware.Adapters.Dav.Core.DavItemId.Resource("calendars/bob/personal/x.ics")).ShouldBeFalse();
        scope.Contains(Nightware.Adapters.Dav.Core.DavItemId.Resource("calendars/alice/personal-other/x.ics")).ShouldBeFalse();
    }

    public void Dispose() => _h.Dispose();
}
