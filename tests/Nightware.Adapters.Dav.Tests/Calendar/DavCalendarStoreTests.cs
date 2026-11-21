using Nightware.Adapters.Dav.Calendar;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using static Nightware.Adapters.Dav.Tests.Calendar.CalendarAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Calendar;

public sealed class DavCalendarStoreTests : IDisposable
{
    private const string Work = AliceHome + "work/";
    private readonly CalendarAdapterHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DavCalendarStoreTests() =>
        _h.Catalog.ListCalendarsAsync(Arg.Is<Nightware.Adapters.Dav.Core.Discovery.DavPrincipal>(p => p.Href == AlicePrincipal.Href), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<DavCalendar>>.Success([Birthdays(), new DavCalendar(Work, "Work") { Components = new HashSet<string>(["VEVENT"]), Color = "#87d28eff", Order = 2 }, Personal(AlicePersonal)]));

    public void Dispose() => _h.Dispose();

    private DavCalendarStore Store() => new(_h.Scopes, _h.Reader, _h.Writer, _h.Dav.Client, _h.Dav.Paths);

    private CalendarId IdOf(string href) => new(DavItemId.Resource(_h.Dav.Paths.ToRelative(href)!).ToString());

    [Fact]
    public async Task Lists_the_default_calendar_first_with_its_colour_and_owner()
    {
        var calendars = (await Store().ListAsync(AliceMailbox, Ct)).Value;

        calendars[0].Name.ShouldBe("Personal");
        calendars[0].IsDefault.ShouldBeTrue();
        calendars[0].IsRemovable.ShouldBeFalse();
        calendars[0].Owner.ShouldBe(new EmailAddress(Alice, "Alice Anderson"));
        var work = calendars.Single(c => c.Name == "Work");
        work.HexColor.ShouldBe("#87D28E");
        work.Id.ShouldBe(IdOf(Work));
        calendars.Single(c => c.Name == "Contact birthdays").CanEdit.ShouldBeFalse();
    }

    [Fact]
    public async Task Create_is_mkcalendar_in_the_callers_home()
    {
        _h.Dav.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(new WireMock.Matchers.WildcardMatcher(AliceHome + "nightware-*")).UsingMethod("MKCALENDAR"))
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(201));

        var created = await Store().CreateAsync(AliceMailbox, "Projects", "#A6D1F5", Ct);

        var mk = _h.Requests("MKCALENDAR").ShouldHaveSingleItem();
        BodyOf(mk).ShouldContain("Projects");
        BodyOf(mk).ShouldContain("#A6D1F5");
        BodyOf(mk).ShouldContain("VEVENT");
        created.Error!.Kind.ShouldBe(ErrorKind.NotFound); // the catalog fake does not learn of it; the live test reads it back
    }

    [Fact]
    public async Task Calendars_are_added_only_to_the_callers_own_mailbox()
    {
        _h.Discovery.DiscoverAsync(BobMailbox, Arg.Any<CancellationToken>()).Returns(BobPrincipal with { IsCurrentUser = false });
        _h.Catalog.ListCalendarsAsync(Arg.Is<Nightware.Adapters.Dav.Core.Discovery.DavPrincipal>(p => p.Href == AlicePrincipal.Href), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<DavCalendar>>.Success([Personal(AlicePersonal), new DavCalendar(AliceHome + "bob-shared/", "Bob's") { Components = new HashSet<string>(["VEVENT"]), IsShared = true, OwnerPrincipal = "principals/users/bob" }]));

        (await Store().CreateAsync(BobMailbox, "x", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Update_proppatches_name_and_colour_and_auto_removes_the_colour()
    {
        _h.Serve("PROPPATCH", Work, 207, """<?xml version="1.0"?><d:multistatus xmlns:d="DAV:"><d:response><d:href>/x</d:href><d:propstat><d:prop/><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>""");

        (await Store().UpdateAsync(AliceMailbox, IdOf(Work), "Office", "#F1878A", Ct)).IsSuccess.ShouldBeTrue();
        (await Store().UpdateAsync(AliceMailbox, IdOf(Work), null, string.Empty, Ct)).IsSuccess.ShouldBeTrue();

        var patches = _h.Requests("PROPPATCH");
        BodyOf(patches[0]).ShouldContain("Office");
        BodyOf(patches[0]).ShouldContain("#F1878A");
        BodyOf(patches[1]).ShouldContain("remove");
    }

    [Fact]
    public async Task Delete_removes_the_collection_and_unknown_ids_are_404()
    {
        _h.Serve("DELETE", Work, 204);

        (await Store().DeleteAsync(AliceMailbox, IdOf(Work), Ct)).IsSuccess.ShouldBeTrue();
        (await Store().DeleteAsync(AliceMailbox, IdOf(AliceHome + "nope/"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Store().DeleteAsync(AliceMailbox, new CalendarId("not-an-id"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Events_of_a_named_calendar_are_read_from_it_and_a_read_only_one_takes_none()
    {
        _h.Serve("REPORT", Work, 207, """<?xml version="1.0"?><d:multistatus xmlns:d="DAV:"/>""");

        (await Store().ListAsync(AliceMailbox, IdOf(Work), ItemQuery.Default, Ct)).Value.Items.ShouldBeEmpty();
        _h.Requests("REPORT").ShouldHaveSingleItem().RequestMessage!.Path.ShouldBe(Work);
        var birthdays = IdOf(AliceHome + "contact_birthdays/");
        (await Store().CreateAsync(AliceMailbox, birthdays, new NewEvent("x", Utc(1), Utc(2)), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    private static DateTimeTimeZone Utc(int hour) => new(new DateTime(2026, 12, 1, hour, 0, 0), "UTC");
}
