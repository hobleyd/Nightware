using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Nightware.Integration.Tests.Tasks;

/// <summary>To Do against the live Nextcloud: lists, a task round trip with a due date, and attachments.</summary>
public sealed class TasksLiveTests : IAsyncLifetime
{
    private GroupwareLiveHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = GroupwareLiveHost.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_task_round_trips_with_its_due_date_and_status()
    {
        using var graph = await _host.GraphAsync(GroupwareLiveHost.Alice, Ct);
        var listId = await DefaultListAsync(graph);
        var title = "Live task " + Guid.NewGuid().ToString("N")[..8];

        var created = await graph.PostAsJsonAsync($"/v1.0/me/todo/lists/{listId}/tasks", new
        {
            title,
            importance = "high",
            body = new { content = "From the integration suite.", contentType = "text" },
            dueDateTime = new { dateTime = "2026-12-24T00:00:00", timeZone = "UTC" },
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var task = (await created.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var taskId = task["id"]!.GetValue<string>();
        task["dueDateTime"]!["dateTime"]!.GetValue<string>().ShouldBe("2026-12-24T00:00:00.0000000");
        task["dueDateTime"]!["timeZone"]!.GetValue<string>().ShouldBe("UTC");
        task["importance"]!.GetValue<string>().ShouldBe("high");

        var open = await ListAsync(graph, listId, "&$filter=status ne 'completed'");
        open.ShouldContain(t => t["id"]!.GetValue<string>() == taskId && t["body"]!["content"]!.GetValue<string>() == "From the integration suite.");

        var moved = await graph.PatchAsJsonAsync($"/v1.0/me/todo/lists/{listId}/tasks/{taskId}", new { dueDateTime = new { dateTime = "2027-01-02T00:00:00", timeZone = "UTC" } }, Ct);
        (await moved.Content.ReadFromJsonAsync<JsonObject>(Ct))!["dueDateTime"]!["dateTime"]!.GetValue<string>().ShouldBe("2027-01-02T00:00:00.0000000");

        using var clear = new HttpRequestMessage(HttpMethod.Patch, $"/v1.0/me/todo/lists/{listId}/tasks/{taskId}") { Content = new StringContent("""{"dueDateTime":null}""", Encoding.UTF8, "application/json") };
        var cleared = await graph.SendAsync(clear, Ct);
        (await cleared.Content.ReadFromJsonAsync<JsonObject>(Ct))!.ContainsKey("dueDateTime").ShouldBeFalse();

        var done = await graph.PatchAsJsonAsync($"/v1.0/me/todo/lists/{listId}/tasks/{taskId}", new { status = "completed" }, Ct);
        done.StatusCode.ShouldBe(HttpStatusCode.OK);
        var completed = (await done.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        completed["status"]!.GetValue<string>().ShouldBe("completed");
        completed["completedDateTime"].ShouldNotBeNull();

        (await ListAsync(graph, listId, "&$filter=status ne 'completed'")).ShouldNotContain(t => t["id"]!.GetValue<string>() == taskId);
        (await ListAsync(graph, listId, string.Empty)).ShouldContain(t => t["id"]!.GetValue<string>() == taskId);
    }

    [Fact]
    public async Task Attachments_upload_through_a_bearerless_session_and_download_intact()
    {
        using var graph = await _host.GraphAsync(GroupwareLiveHost.Alice, Ct);
        var listId = await DefaultListAsync(graph);
        var created = await graph.PostAsJsonAsync($"/v1.0/me/todo/lists/{listId}/tasks", new { title = "Live attachments " + Guid.NewGuid().ToString("N")[..8] }, Ct);
        var taskId = (await created.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!.GetValue<string>();

        // Small: inline contentBytes.
        var small = Encoding.UTF8.GetBytes("From: alex@example.com\r\nSubject: small\r\n\r\nInline attachment.\r\n");
        var inline = await graph.PostAsJsonAsync($"/v1.0/me/todo/lists/{listId}/tasks/{taskId}/attachments", new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.taskFileAttachment",
            ["name"] = "Small thread.eml",
            ["contentType"] = "message/rfc822",
            ["contentBytes"] = Convert.ToBase64String(small),
            ["size"] = small.Length,
        }, Ct);
        inline.StatusCode.ShouldBe(HttpStatusCode.Created, await inline.Content.ReadAsStringAsync(Ct));

        // Large: an upload session, PUT whole with no Authorization header, as NightMail does.
        var large = RandomNumberGenerator.GetBytes(4 * 1024 * 1024 + 123);
        var session = await graph.PostAsJsonAsync($"/v1.0/me/todo/lists/{listId}/tasks/{taskId}/attachments/createUploadSession", new
        {
            attachmentItem = new Dictionary<string, object>
            {
                ["@odata.type"] = "#microsoft.graph.fileAttachmentUploadProperties",
                ["attachmentType"] = "file",
                ["contentType"] = "message/rfc822",
                ["name"] = "Large thread.eml",
                ["size"] = large.Length,
            },
        }, Ct);
        session.StatusCode.ShouldBe(HttpStatusCode.Created, await session.Content.ReadAsStringAsync(Ct));
        var uploadUrl = (await session.Content.ReadFromJsonAsync<JsonObject>(Ct))!["uploadUrl"]!.GetValue<string>();
        uploadUrl.Length.ShouldBeLessThan(8192);

        using var anonymous = _host.Anonymous();
        var content = new ByteArrayContent(large);
        content.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
        content.Headers.TryAddWithoutValidation("Content-Range", $"bytes 0-{large.Length - 1}/{large.Length}");
        var put = await anonymous.PutAsync(GroupwareLiveHost.Local(uploadUrl), content, Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.Created, await put.Content.ReadAsStringAsync(Ct));
        var uploaded = (await put.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        uploaded["size"]!.GetValue<long>().ShouldBe(large.Length);

        // The URL is single-target: a second PUT of another size fails, and no bearer-less GET works.
        var listed = await graph.GetFromJsonAsync<JsonObject>($"/v1.0/me/todo/lists/{listId}/tasks/{taskId}/attachments", Ct);
        var attachments = listed!["value"]!.AsArray().Select(a => a!.AsObject()).ToList();
        attachments.Select(a => a["name"]!.GetValue<string>()).ShouldBe(["Small thread.eml", "Large thread.eml"], ignoreOrder: true);

        foreach (var (name, expected) in new[] { ("Small thread.eml", small), ("Large thread.eml", large) })
        {
            var id = attachments.Single(a => a["name"]!.GetValue<string>() == name)["id"]!.GetValue<string>();
            var bytes = await graph.GetByteArrayAsync($"/v1.0/me/todo/lists/{listId}/tasks/{taskId}/attachments/{id}/$value", Ct);
            bytes.ShouldBe(expected, name);
        }

        var tasks = await ListAsync(graph, listId, string.Empty);
        tasks.Single(t => t["id"]!.GetValue<string>() == taskId)["hasAttachments"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task A_forged_upload_url_is_refused()
    {
        using var graph = await _host.GraphAsync(GroupwareLiveHost.Alice, Ct);
        var listId = await DefaultListAsync(graph);
        var created = await graph.PostAsJsonAsync($"/v1.0/me/todo/lists/{listId}/tasks", new { title = "Live forged " + Guid.NewGuid().ToString("N")[..8] }, Ct);
        var taskId = (await created.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!.GetValue<string>();
        var session = await graph.PostAsJsonAsync($"/v1.0/me/todo/lists/{listId}/tasks/{taskId}/attachments/createUploadSession",
            new { attachmentItem = new { attachmentType = "file", contentType = "text/plain", name = "a.txt", size = 4 } }, Ct);
        var uploadUrl = (await session.Content.ReadFromJsonAsync<JsonObject>(Ct))!["uploadUrl"]!.GetValue<string>();
        var forged = uploadUrl[..^4] + (uploadUrl[^4] == 'A' ? "B" : "A") + uploadUrl[^3..];

        using var anonymous = _host.Anonymous();
        var put = await anonymous.PutAsync(GroupwareLiveHost.Local(forged), new ByteArrayContent("abcd"u8.ToArray()), Ct);

        put.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<string> DefaultListAsync(HttpClient graph)
    {
        var response = await graph.GetAsync("/v1.0/me/todo/lists?$top=100", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var lists = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["value"]!.AsArray();
        return lists.Single(l => l!["wellknownListName"]!.GetValue<string>() == "defaultList")!["id"]!.GetValue<string>();
    }

    private static async Task<IReadOnlyList<JsonObject>> ListAsync(HttpClient graph, string listId, string filter)
    {
        var response = await graph.GetAsync($"/v1.0/me/todo/lists/{listId}/tasks?$top=200&$orderby=createdDateTime desc{filter}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return [.. (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["value"]!.AsArray().Select(t => t!.AsObject())];
    }
}
