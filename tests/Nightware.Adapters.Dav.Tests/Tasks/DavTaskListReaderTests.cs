using System.Xml.Linq;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

using SortDirection = Nightware.Application.Common.Query.SortDirection;

namespace Nightware.Adapters.Dav.Tests.Tasks;

public sealed class DavTaskListReaderTests : IDisposable
{
    private readonly TaskAdapterHarness _h = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_the_task_collections_with_the_default_first()
    {
        _h.ServeHome();

        var page = (await _h.Lists.ListAsync(TaskAdapterHarness.Alice, new ItemQuery { Top = 100 }, Ct)).Value;

        page.Items.Select(l => l.DisplayName).ShouldBe(["Tasks", "Team calendar (Bob)"]);
        var tasks = page.Items[0];
        tasks.Id.ShouldBe(TaskAdapterHarness.ListId());
        tasks.WellKnownListName.ShouldBe("defaultList");
        tasks.IsOwner.ShouldBeTrue();
        tasks.IsShared.ShouldBeFalse();
        var shared = page.Items[1];
        shared.Id.ShouldBe(TaskAdapterHarness.ListId(TaskAdapterHarness.SharedList));
        shared.WellKnownListName.ShouldBe("none");
        shared.IsOwner.ShouldBeFalse();
        shared.IsShared.ShouldBeTrue();
        page.SkipToken.ShouldBeNull();
        _h.Requests("MKCALENDAR").ShouldBeEmpty();
    }

    [Fact]
    public async Task Pages_with_top()
    {
        _h.ServeHome();

        var page = (await _h.Lists.ListAsync(TaskAdapterHarness.Alice, new ItemQuery { Top = 1 }, Ct)).Value;

        page.Items.ShouldHaveSingleItem().DisplayName.ShouldBe("Tasks");
        page.SkipToken.ShouldBe("1");
    }

    [Fact]
    public async Task Creates_the_default_list_when_the_caller_owns_none()
    {
        _h.Dav.Server.Given(Request.Create().WithPath(TaskAdapterHarness.Home).UsingMethod("PROPFIND"))
            .InScenario("provision").WillSetStateTo("listed")
            .RespondWith(Response.Create().WithStatusCode(207).WithBody(Fixture.Read("Tasks/propfind-calendar-home-no-tasks.xml")));
        _h.Dav.Server.Given(Request.Create().WithPath(TaskAdapterHarness.Home).UsingMethod("PROPFIND"))
            .InScenario("provision").WhenStateIs("listed")
            .RespondWith(Response.Create().WithStatusCode(207).WithBody(Fixture.Read("propfind-calendar-home.xml")));
        _h.Respond("MKCALENDAR", TaskAdapterHarness.TasksList, 201);

        var page = (await _h.Lists.ListAsync(TaskAdapterHarness.Alice, ItemQuery.Default, Ct)).Value;

        page.Items[0].WellKnownListName.ShouldBe("defaultList");
        var body = XDocument.Parse(TaskAdapterHarness.BodyOf(_h.Requests("MKCALENDAR").ShouldHaveSingleItem()));
        body.Descendants(DavNames.DisplayName).Single().Value.ShouldBe("Tasks");
        body.Descendants(DavNames.Comp).Select(c => (string?)c.Attribute("name")).ShouldBe(["VTODO"]);
    }

    [Fact]
    public async Task An_existing_tasks_collection_is_not_an_error_when_provisioning()
    {
        _h.ServeHome("Tasks/propfind-calendar-home-no-tasks.xml");
        _h.Respond("MKCALENDAR", TaskAdapterHarness.TasksList, 405);

        var page = (await _h.Lists.ListAsync(TaskAdapterHarness.Alice, ItemQuery.Default, Ct)).Value;

        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Provisioning_failures_pass_through()
    {
        _h.ServeHome("Tasks/propfind-calendar-home-no-tasks.xml");
        _h.Respond("MKCALENDAR", TaskAdapterHarness.TasksList, 403);

        var result = await _h.Lists.ListAsync(TaskAdapterHarness.Alice, ItemQuery.Default, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Never_creates_a_list_in_someone_elses_home()
    {
        using var h = new TaskAdapterHarness(TaskAdapterHarness.AlicePrincipal with { IsCurrentUser = false });
        h.ServeHome("Tasks/propfind-calendar-home-no-tasks.xml");

        var page = (await h.Lists.ListAsync(new MailboxRef("team@nightware.localhost"), ItemQuery.Default, Ct)).Value;

        page.Items.ShouldBeEmpty();
        h.Requests("MKCALENDAR").ShouldBeEmpty();
    }

    [Fact]
    public async Task Discovery_failures_pass_through()
    {
        _h.Discovery.DiscoverAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no principal"));

        var result = await _h.Lists.ListAsync(TaskAdapterHarness.Alice, ItemQuery.Default, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Backend_failures_pass_through()
    {
        _h.Respond("PROPFIND", TaskAdapterHarness.Home, 503);

        var result = await _h.Lists.ListAsync(TaskAdapterHarness.Alice, ItemQuery.Default, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Filters_and_orderings_are_refused()
    {
        var result = await _h.Lists.ListAsync(TaskAdapterHarness.Alice, new ItemQuery { OrderBy = [new OrderBy("displayName", SortDirection.Ascending)] }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        _h.Dav.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_principal_without_a_calendar_home_has_no_lists()
    {
        using var h = new TaskAdapterHarness(new DavPrincipal("/remote.php/dav/principals/users/alice/", true));

        var result = await h.Lists.ListAsync(TaskAdapterHarness.Alice, ItemQuery.Default, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    public void Dispose() => _h.Dispose();
}
