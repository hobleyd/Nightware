using System.Xml.Linq;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using TaskStatus = Nightware.Domain.Tasks.TaskStatus;

using SortDirection = Nightware.Application.Common.Query.SortDirection;

namespace Nightware.Adapters.Dav.Tests.Tasks;

public sealed class DavTaskStoreTests : IDisposable
{
    private readonly TaskAdapterHarness _h = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ItemQuery ClientQuery = new()
    {
        Top = 200,
        OrderBy = [new OrderBy("createdDateTime", SortDirection.Descending)],
        Filter = new FilterNode.Compare("status", CompareOp.Ne, new FilterValue.Text("completed")),
    };

    [Fact]
    public async Task Lists_open_tasks_newest_first_with_a_vtodo_calendar_query()
    {
        _h.Respond("REPORT", TaskAdapterHarness.TasksList, 207, Fixture.Read("Tasks/calendar-query-tasks.xml"));

        var page = (await _h.Store.ListAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), ClientQuery, Ct)).Value;

        page.Items.Select(t => t.Title).ShouldBe(["Water the plants", "Reply to Alex about Q2 numbers"]);
        var reply = page.Items[1];
        reply.Id.ShouldBe(TaskAdapterHarness.TaskIdOf());
        reply.DueDateTime.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 5), "UTC"));
        reply.BodyText.ShouldBe("See the email from Alex.");
        var request = _h.Requests("REPORT").ShouldHaveSingleItem();
        TaskAdapterHarness.Header(request, "Depth").ShouldBe("1");
        var filter = XDocument.Parse(TaskAdapterHarness.BodyOf(request)).Descendants(DavNs.CalDav + "comp-filter").Select(c => (string?)c.Attribute("name"));
        filter.ShouldBe(["VCALENDAR", "VTODO"]);
    }

    [Fact]
    public async Task Lists_every_task_without_a_filter()
    {
        _h.Respond("REPORT", TaskAdapterHarness.TasksList, 207, Fixture.Read("Tasks/calendar-query-tasks.xml"));

        var page = (await _h.Store.ListAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), new ItemQuery(), Ct)).Value;

        page.Items.Count.ShouldBe(3);
        page.Items.Single(t => t.Title == "Book flights").Status.ShouldBe(TaskStatus.Completed);
    }

    [Fact]
    public async Task Ignores_members_that_are_not_tasks_of_the_list()
    {
        var foreign = Fixture.Read("Tasks/calendar-query-tasks.xml")
            .Replace("/remote.php/dav/calendars/alice/tasks/c0ffee00", "/remote.php/dav/calendars/alice/personal/c0ffee00", StringComparison.Ordinal);
        _h.Respond("REPORT", TaskAdapterHarness.TasksList, 207, foreign);

        var page = (await _h.Store.ListAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), new ItemQuery(), Ct)).Value;

        page.Items.Select(t => t.Title).ShouldNotContain("Water the plants");
    }

    [Fact]
    public async Task Rejects_a_list_outside_the_mailboxs_calendar_home_without_a_request()
    {
        var bobs = DavItemId.Resource("calendars/bob/tasks/").ToTaskListId();

        var result = await _h.Store.ListAsync(TaskAdapterHarness.Alice, bobs, new ItemQuery(), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _h.Dav.Server.LogEntries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("")]
    public async Task Rejects_malformed_list_ids(string id)
    {
        var result = await _h.Store.ListAsync(TaskAdapterHarness.Alice, new TaskListId(id), new ItemQuery(), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Rejects_an_unsupported_filter()
    {
        _h.Respond("REPORT", TaskAdapterHarness.TasksList, 207, Fixture.Read("Tasks/calendar-query-tasks.xml"));

        var result = await _h.Store.ListAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(),
            new ItemQuery { Filter = new FilterNode.Compare("importance", CompareOp.Eq, new FilterValue.Text("high")) }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Listing_failures_pass_through()
    {
        _h.Respond("REPORT", TaskAdapterHarness.TasksList, 404);

        var result = await _h.Store.ListAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), new ItemQuery(), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Gets_one_task()
    {
        _h.ServeTask(TaskAdapterHarness.Ics());

        var task = (await _h.Store.GetAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), Ct)).Value;

        task.Title.ShouldBe("Reply to Alex about Q2 numbers");
        task.Id.ShouldBe(TaskAdapterHarness.TaskIdOf());
    }

    [Theory]
    [InlineData("calendars/alice/personal/0f1e.ics")]
    [InlineData("calendars/alice/tasks/nested/0f1e.ics")]
    [InlineData("calendars/alice/tasks/")]
    public async Task Rejects_a_task_that_is_not_directly_in_the_list(string href)
    {
        var result = await _h.Store.GetAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), DavItemId.Resource(href).ToTaskId(), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _h.Dav.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_of_a_missing_task_is_not_found()
    {
        _h.Respond("GET", TaskAdapterHarness.OpenTask, 404);

        var result = await _h.Store.GetAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Creates_a_task_as_a_new_resource_with_a_date_due()
    {
        _h.Dav.Server.Given(Request.Create().WithPath(new WireMock.Matchers.WildcardMatcher(TaskAdapterHarness.TasksList + "*.ics")).UsingPut())
            .RespondWith(Response.Create().WithStatusCode(201));
        var draft = new TaskDraft
        {
            Title = "Reply to Alex",
            Importance = TaskImportance.High,
            BodyText = "See the email.",
            DueDateTime = new DateTimeTimeZone(new DateTime(2026, 6, 5), "UTC"),
        };

        var task = (await _h.Store.CreateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), draft, Ct)).Value;

        var put = _h.Requests("PUT").ShouldHaveSingleItem();
        put.RequestMessage!.Path.ShouldMatch(@"^/remote\.php/dav/calendars/alice/tasks/[0-9a-f]{32}\.ics$");
        TaskAdapterHarness.Header(put, "If-None-Match").ShouldBe("*");
        var ics = TaskAdapterHarness.BodyOf(put);
        ics.ShouldContain("SUMMARY:Reply to Alex");
        ics.ShouldContain("DUE;VALUE=DATE:20260605");
        ics.ShouldContain("PRIORITY:1");
        task.Id.ShouldBe(DavItemId.Resource(put.RequestMessage.Path["/remote.php/dav/".Length..]).ToTaskId());
        task.Status.ShouldBe(TaskStatus.NotStarted);
        task.Importance.ShouldBe(TaskImportance.High);
        task.DueDateTime.ShouldBe(draft.DueDateTime);
        task.CreatedDateTime.ShouldBe(_h.Dav.Time.GetUtcNow());
    }

    [Fact]
    public async Task Create_failures_pass_through()
    {
        _h.Dav.Server.Given(Request.Create().WithPath(new WireMock.Matchers.WildcardMatcher(TaskAdapterHarness.TasksList + "*")).UsingPut())
            .RespondWith(Response.Create().WithStatusCode(403));

        var result = await _h.Store.CreateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), new TaskDraft { Title = "x" }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Updates_in_place_under_if_match()
    {
        _h.ServeTask(TaskAdapterHarness.Ics(extra: "X-APPLE-SORT-ORDER:42\r\n"));
        _h.Respond("PUT", TaskAdapterHarness.OpenTask, 204);

        var task = (await _h.Store.UpdateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), new TaskDraft { Status = TaskStatus.Completed }, Ct)).Value;

        task.Status.ShouldBe(TaskStatus.Completed);
        task.CompletedDateTime.ShouldNotBeNull();
        var put = _h.Requests("PUT").ShouldHaveSingleItem();
        TaskAdapterHarness.Header(put, "If-Match").ShouldBe("\"etag-1\"");
        var ics = TaskAdapterHarness.BodyOf(put);
        ics.ShouldContain("STATUS:COMPLETED");
        ics.ShouldContain("X-APPLE-SORT-ORDER:42");
    }

    [Fact]
    public async Task Clears_the_due_date()
    {
        _h.ServeTask(TaskAdapterHarness.Ics());
        _h.Respond("PUT", TaskAdapterHarness.OpenTask, 204);

        var task = (await _h.Store.UpdateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), new TaskDraft { ClearDueDateTime = true }, Ct)).Value;

        task.DueDateTime.ShouldBeNull();
        TaskAdapterHarness.BodyOf(_h.Requests("PUT").Single()).ShouldNotContain("DUE");
    }

    [Fact]
    public async Task Retries_once_when_the_task_changed_underneath()
    {
        _h.ServeTask(TaskAdapterHarness.Ics());
        _h.Dav.Server.Given(Request.Create().WithPath(TaskAdapterHarness.OpenTask).UsingPut())
            .InScenario("race").WillSetStateTo("retried")
            .RespondWith(Response.Create().WithStatusCode(412));
        _h.Dav.Server.Given(Request.Create().WithPath(TaskAdapterHarness.OpenTask).UsingPut())
            .InScenario("race").WhenStateIs("retried")
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = await _h.Store.UpdateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), new TaskDraft { Title = "New" }, Ct);

        result.Value.Title.ShouldBe("New");
        _h.Requests("GET").Count.ShouldBe(2);
        _h.Requests("PUT").Count.ShouldBe(2);
    }

    [Fact]
    public async Task Gives_up_after_a_second_conflict()
    {
        _h.ServeTask(TaskAdapterHarness.Ics());
        _h.Respond("PUT", TaskAdapterHarness.OpenTask, 412);

        var result = await _h.Store.UpdateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), new TaskDraft { Title = "New" }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        _h.Requests("PUT").Count.ShouldBe(2);
    }

    [Fact]
    public async Task Update_without_an_etag_writes_unconditionally()
    {
        _h.Respond("GET", TaskAdapterHarness.OpenTask, 200, TaskAdapterHarness.Ics());
        _h.Respond("PUT", TaskAdapterHarness.OpenTask, 204);

        var result = await _h.Store.UpdateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), new TaskDraft { Title = "New" }, Ct);

        result.IsSuccess.ShouldBeTrue();
        TaskAdapterHarness.Header(_h.Requests("PUT").Single(), "If-Match").ShouldBeNull();
    }

    [Fact]
    public async Task Update_of_a_resource_without_a_task_is_not_found()
    {
        _h.ServeTask("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nEND:VCALENDAR\r\n");

        var result = await _h.Store.UpdateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), new TaskDraft { Title = "New" }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _h.Requests("PUT").ShouldBeEmpty();
    }

    [Fact]
    public async Task Deletes_a_task()
    {
        _h.Respond("DELETE", TaskAdapterHarness.OpenTask, 204);

        var result = await _h.Store.DeleteAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), Ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_of_a_foreign_task_sends_nothing()
    {
        var result = await _h.Store.DeleteAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), DavItemId.Resource("calendars/bob/tasks/x.ics").ToTaskId(), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _h.Dav.Server.LogEntries.ShouldBeEmpty();
    }

    public void Dispose() => _h.Dispose();
}
