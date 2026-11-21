using System.Security;
using Nightware.Adapters.Dav.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Tasks;

public sealed class DavTaskDeltaTests : IDisposable
{
    private const string List = TaskAdapterHarness.TasksList;
    private const string Parent = List + "parent.ics";
    private const string Child = List + "child.ics";
    private readonly TaskAdapterHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    private DavTaskDelta Delta => new(_h.Collections, _h.Store, _h.Dav.Client, _h.Dav.Paths);

    private static string Todo(string uid, string extra = "") =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20261121T000000Z\r\nSUMMARY:{uid}\r\n{extra}END:VTODO\r\nEND:VCALENDAR\r\n";

    private void ServeList() =>
        _h.Dav.Server.Given(Request.Create().WithPath(List).UsingMethod("REPORT").WithBody(b => b!.Contains("calendar-query", StringComparison.Ordinal)))
            .RespondWith(Response.Create().WithStatusCode(207).WithBody(
                $"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:" xmlns:cal="urn:ietf:params:xml:ns:caldav">{string.Concat(new[] { (Parent, Todo("p")), (Child, Todo("c", "RELATED-TO:p\r\n")) }.Select(t =>
                    $"<d:response><d:href>{t.Item1}</d:href><d:propstat><d:prop><d:getetag>\"1\"</d:getetag><cal:calendar-data>{SecurityElement.Escape(t.Item2)}</cal:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"))}</d:multistatus>"""));

    private void ServeSync(string? from, string responses, string next) =>
        _h.Dav.Server.Given(Request.Create().WithPath(List).UsingMethod("REPORT").WithBody(b => b!.Contains("sync-collection", StringComparison.Ordinal)
                && (from is null ? !b.Contains("sync/", StringComparison.Ordinal) : b.Contains(from, StringComparison.Ordinal))))
            .RespondWith(Response.Create().WithStatusCode(207).WithBody(
                $"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:">{responses}<d:sync-token>{next}</d:sync-token></d:multistatus>"""));

    private static string Found(string href) =>
        $"<d:response><d:href>{href}</d:href><d:propstat><d:prop><d:getetag>\"1\"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

    private static string Gone(string href) => $"<d:response><d:href>{href}</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>";

    [Fact]
    public async Task The_initial_sync_is_every_task_as_a_first_page_without_checklist_items()
    {
        _h.ServeHome();
        ServeList();
        ServeSync(null, Found(List) + Found(Parent) + Found(Child), "sync/1");

        var page = (await Delta.DeltaAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), null, Ct)).Value;

        page.Entries.OfType<DeltaEntry<TodoTask>.Upsert>().ShouldHaveSingleItem().Item.Title.ShouldBe("p");
        page.NextPage.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_changed_checklist_item_stands_for_its_task_and_removals_follow()
    {
        _h.ServeHome();
        ServeList();
        ServeSync(null, Found(Parent) + Found(Child), "sync/1");
        var token = (await Delta.DeltaAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), null, Ct)).Value.NextPage!.Value;
        ServeSync("sync/1", Found(Child) + Gone(List + "old.ics"), "sync/2");

        var page = (await Delta.DeltaAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), token, Ct)).Value;

        page.Entries.OfType<DeltaEntry<TodoTask>.Upsert>().ShouldHaveSingleItem().Item.Title.ShouldBe("p");
        page.Entries.OfType<DeltaEntry<TodoTask>.Removed>().ShouldHaveSingleItem().Id.ShouldBe(TaskAdapterHarness.TaskIdOf(List + "old.ics").Value);
        page.Final.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_token_for_another_list_or_garbage_is_gone()
    {
        _h.ServeHome();
        ServeList();
        ServeSync(null, Found(List), "sync/1");
        var token = (await Delta.DeltaAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), null, Ct)).Value.NextPage!.Value;

        (await Delta.DeltaAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(TaskAdapterHarness.SharedList), token, Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
        (await Delta.DeltaAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), new DeltaToken("!!"), Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }
}
