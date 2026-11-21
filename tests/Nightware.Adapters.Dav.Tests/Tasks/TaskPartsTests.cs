using System.Security;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Adapters.Dav.Tests.Tasks;

/// <summary>Checklist items (subtasks), linked resources, and how both affect the task listing and delete.</summary>
public sealed class TaskPartsTests : IDisposable
{
    private const string Parent = TaskAdapterHarness.TasksList + "parent.ics";
    private const string Child = TaskAdapterHarness.TasksList + "child.ics";
    private const string Grandchild = TaskAdapterHarness.TasksList + "grandchild.ics";
    private const string Orphan = TaskAdapterHarness.TasksList + "orphan.ics";
    private readonly TaskAdapterHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    private static string Todo(string uid, string extra = "") =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20261121T000000Z\r\nCREATED:20261121T000000Z\r\nSUMMARY:{uid}\r\n{extra}END:VTODO\r\nEND:VCALENDAR\r\n";

    private void ServeList(params (string Href, string Ics)[] todos) =>
        _h.Respond("REPORT", TaskAdapterHarness.TasksList, 207,
            $"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:" xmlns:cal="urn:ietf:params:xml:ns:caldav">{string.Concat(todos.Select(t =>
                $"<d:response><d:href>{t.Href}</d:href><d:propstat><d:prop><d:getetag>\"1\"</d:getetag><cal:calendar-data>{SecurityElement.Escape(t.Ics)}</cal:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"))}</d:multistatus>""");

    private void ServeFamily() => ServeList(
        (Parent, Todo("p")),
        (Child, Todo("c", "RELATED-TO:p\r\n")),
        (Grandchild, Todo("g", "RELATED-TO;RELTYPE=PARENT:c\r\n")),
        (Orphan, Todo("o", "RELATED-TO:gone\r\n")));

    [Fact]
    public async Task Checklist_items_are_hidden_from_the_list_but_orphans_are_not()
    {
        _h.ServeHome();
        ServeFamily();

        var titles = (await _h.Store.ListAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), new ItemQuery { Top = 100 }, Ct)).Value.Items.Select(t => t.Title);

        titles.ShouldBe(["o", "p"], ignoreOrder: true);
    }

    [Fact]
    public async Task Deleting_a_task_deletes_its_subtasks_deepest_first()
    {
        _h.ServeHome();
        ServeFamily();
        foreach (var href in new[] { Parent, Child, Grandchild })
        {
            _h.Respond("DELETE", href, 204);
        }

        (await _h.Store.DeleteAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(Parent), Ct)).IsSuccess.ShouldBeTrue();

        _h.Requests("DELETE").Select(r => r.RequestMessage!.Path).ShouldBe([Grandchild, Child, Parent]);
    }

    [Fact]
    public async Task A_tasks_checklist_is_its_direct_subtasks()
    {
        _h.ServeHome();
        ServeFamily();
        _h.ServeTask(Todo("p"), Parent);

        var items = (await _h.Checklist.ListAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(Parent), Ct)).Value;

        var item = items.ShouldHaveSingleItem();
        item.DisplayName.ShouldBe("c");
        item.Id.ShouldBe(TaskAdapterHarness.TaskIdOf(Child).Value);
        item.IsChecked.ShouldBeFalse();
        item.CheckedDateTime.ShouldBeNull();
    }

    [Fact]
    public async Task A_new_item_is_a_subtask_written_as_nextcloud_tasks_writes_one()
    {
        _h.ServeHome();
        _h.ServeTask(Todo("p"), Parent);
        _h.Dav.Server.Given(WireMock.RequestBuilders.Request.Create().UsingPut()).RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(201));

        var item = (await _h.Checklist.CreateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(Parent), "Step one", true, Ct)).Value;

        var body = TaskAdapterHarness.BodyOf(_h.Requests("PUT").ShouldHaveSingleItem());
        body.ShouldContain("RELATED-TO:p");
        body.ShouldContain("SUMMARY:Step one");
        body.ShouldContain("STATUS:COMPLETED");
        body.ShouldContain("PERCENT-COMPLETE:100");
        item.IsChecked.ShouldBeTrue();
        item.CheckedDateTime.ShouldNotBeNull();
    }

    [Fact]
    public async Task Checking_off_an_item_edits_it_and_another_tasks_subtask_is_not_found()
    {
        _h.ServeHome();
        _h.ServeTask(Todo("p"), Parent);
        _h.ServeTask(Todo("c", "RELATED-TO:p\r\n"), Child);
        _h.ServeTask(Todo("o", "RELATED-TO:gone\r\n"), Orphan);
        _h.Respond("PUT", Child, 204);

        var item = (await _h.Checklist.UpdateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(Parent), TaskAdapterHarness.TaskIdOf(Child).Value, null, true, Ct)).Value;

        item.IsChecked.ShouldBeTrue();
        TaskAdapterHarness.BodyOf(_h.Requests("PUT").ShouldHaveSingleItem()).ShouldContain("STATUS:COMPLETED");
        (await _h.Checklist.UpdateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(Parent), TaskAdapterHarness.TaskIdOf(Orphan).Value, "x", null, Ct))
            .Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _h.Checklist.DeleteAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(Parent), "garbage", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Linked_resources_round_trip_through_the_vtodo()
    {
        _h.ServeHome();
        _h.ServeTask(Todo("p"), Parent);
        _h.Respond("PUT", Parent, 204);
        var resource = new LinkedResource(string.Empty) { WebUrl = "https://example.com/items/1", ApplicationName = "App; \"quoted\", é", DisplayName = "Item 1", ExternalId = "item-1" };

        var created = (await _h.LinkedResources.CreateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(Parent), resource, Ct)).Value;

        Guid.TryParse(created.Id, out _).ShouldBeTrue();
        var written = TaskAdapterHarness.BodyOf(_h.Requests("PUT").ShouldHaveSingleItem());
        written.ShouldContain("X-NIGHTWARE-LINKED-RESOURCE:");

        _h.Dav.Server.Reset();
        _h.ServeHome();
        _h.ServeTask(written, Parent);
        _h.Respond("PUT", Parent, 204);
        var listed = (await _h.LinkedResources.ListAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(Parent), Ct)).Value.ShouldHaveSingleItem();
        listed.ShouldBe(resource with { Id = created.Id });

        var renamed = (await _h.LinkedResources.UpdateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(Parent), created.Id, new LinkedResource(created.Id) { DisplayName = "Item 1 renamed" }, Ct)).Value;
        renamed.ShouldBe(listed with { DisplayName = "Item 1 renamed" });
        (await _h.LinkedResources.DeleteAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(Parent), "nope", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}
