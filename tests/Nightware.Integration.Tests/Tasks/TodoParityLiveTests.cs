using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Nightware.Integration.Tests.Directory;

namespace Nightware.Integration.Tests.Tasks;

/// <summary>
/// Phase 15 To Do against the running stack. Everything happens in one new list per run: Nextcloud limits
/// calendar creation (about ten an hour), and a list of its own keeps clear of the other task tests.
/// </summary>
public sealed class TodoParityLiveTests : IAsyncLifetime
{
    private const string Alice = "alice";
    private LiveStack _stack = null!;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _stack = LiveStack.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stack is not null)
        {
            await _stack.DisposeAsync();
        }
    }

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string url, string? json = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>An id minted by the DAV adapter is the resource's href, base64url-encoded behind an "r:" kind marker.</summary>
    private static string DavUrl(string id)
    {
        var padded = id.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return $"{LiveStack.CloudBase}/remote.php/dav/{Encoding.UTF8.GetString(Convert.FromBase64String(padded))["r:".Length..]}";
    }

    /// <summary>A raw CalDAV request as Nextcloud's own apps make them (not through the façade).</summary>
    private async Task<(HttpStatusCode Status, string Body)> RawAsync(HttpMethod method, string url, string? ics = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _stack.NextcloudTokenAsync(Alice, Ct));
        request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
        if (ics is not null)
        {
            request.Content = new StringContent(ics, Encoding.UTF8, "text/calendar");
        }

        using var response = await _stack.Backchannel.SendAsync(request, Ct);
        return (response.StatusCode, (await response.Content.ReadAsStringAsync(Ct)).Replace("\r\n ", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_list_of_its_own_holds_a_task_with_its_attachment_checklist_and_links_until_all_are_deleted()
    {
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var lists = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/todo/lists");
        var defaultId = lists["value"]!.AsArray().Single(l => l!["wellknownListName"]!.GetValue<string>() == "defaultList")!["id"]!.GetValue<string>();
        await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/todo/lists/{defaultId}", expected: HttpStatusCode.BadRequest);

        var list = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/todo/lists", $$"""{"displayName":"Live {{_run}}"}""", HttpStatusCode.Created);
        var listId = list["id"]!.GetValue<string>();
        try
        {
            list["wellknownListName"]!.GetValue<string>().ShouldBe("none");
            (await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/todo/lists/{listId}", $$"""{"displayName":"Live {{_run}} renamed"}"""))["displayName"]!.GetValue<string>()
                .ShouldBe($"Live {_run} renamed");
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/todo/lists/{listId}"))["displayName"]!.GetValue<string>().ShouldBe($"Live {_run} renamed");

            var task = await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/todo/lists/{listId}/tasks", """{"title":"Live task","importance":"high"}""", HttpStatusCode.Created);
            var taskId = task["id"]!.GetValue<string>();
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/todo/lists/{listId}/tasks/{taskId}"))["importance"]!.GetValue<string>().ShouldBe("high");

            var bytes = Encoding.UTF8.GetBytes("Nightware live attachment");
            var attachment = await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/todo/lists/{listId}/tasks/{taskId}/attachments", $$"""
                {"@odata.type":"#microsoft.graph.taskFileAttachment","name":"note {{_run}}.txt","contentType":"text/plain","contentBytes":"{{Convert.ToBase64String(bytes)}}"}
                """, HttpStatusCode.Created);
            var attachmentUrl = $"/v1.0/me/todo/lists/{listId}/tasks/{taskId}/attachments/{attachment["id"]!.GetValue<string>()}";
            var read = await SendAsync(alice, HttpMethod.Get, attachmentUrl);
            Convert.FromBase64String(read["contentBytes"]!.GetValue<string>()).ShouldBe(bytes);
            await SendAsync(alice, HttpMethod.Delete, attachmentUrl, expected: HttpStatusCode.NoContent);
            await SendAsync(alice, HttpMethod.Get, attachmentUrl, expected: HttpStatusCode.NotFound);

            // Two checklist items are two subtasks in Nextcloud, hidden from the task listing.
            var checklist = $"/v1.0/me/todo/lists/{listId}/tasks/{taskId}/checklistItems";
            var one = await SendAsync(alice, HttpMethod.Post, checklist, """{"displayName":"Step one"}""", HttpStatusCode.Created);
            await SendAsync(alice, HttpMethod.Post, checklist, """{"displayName":"Step two"}""", HttpStatusCode.Created);
            var oneUrl = DavUrl(one["id"]!.GetValue<string>());
            var (_, stored) = await RawAsync(HttpMethod.Get, oneUrl);
            stored.ShouldContain("RELATED-TO:");
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/todo/lists/{listId}/tasks"))["value"]!.AsArray().ShouldHaveSingleItem();

            // Checked off in Nextcloud (as its Tasks app does), seen over Graph.
            var completed = stored.Replace("STATUS:NEEDS-ACTION", "STATUS:COMPLETED\r\nCOMPLETED:20261121T010203Z\r\nPERCENT-COMPLETE:100", StringComparison.Ordinal);
            (await RawAsync(HttpMethod.Put, oneUrl, completed)).Status.ShouldBeOneOf(HttpStatusCode.NoContent, HttpStatusCode.Created, HttpStatusCode.OK);
            var items = (await SendAsync(alice, HttpMethod.Get, checklist))["value"]!.AsArray();
            items.Count.ShouldBe(2);
            var checkedItem = items.Single(i => i!["displayName"]!.GetValue<string>() == "Step one")!;
            checkedItem["isChecked"]!.GetValue<bool>().ShouldBeTrue();
            checkedItem["checkedDateTime"]!.GetValue<string>().ShouldBe("2026-11-21T01:02:03Z");
            var cleared = await SendAsync(alice, HttpMethod.Patch, $"{checklist}/{one["id"]!.GetValue<string>()}", """{"isChecked":false}""");
            cleared["isChecked"]!.GetValue<bool>().ShouldBeFalse();
            cleared.ContainsKey("checkedDateTime").ShouldBeFalse();

            var linked = $"/v1.0/me/todo/lists/{listId}/tasks/{taskId}/linkedResources";
            var link = await SendAsync(alice, HttpMethod.Post, linked, """{"webUrl":"https://example.com/items/1","applicationName":"Live; \"app\"","displayName":"Item 1","externalId":"item-1"}""", HttpStatusCode.Created);
            (await SendAsync(alice, HttpMethod.Patch, $"{linked}/{link["id"]!.GetValue<string>()}", """{"displayName":"Item 1 renamed"}"""))["displayName"]!.GetValue<string>().ShouldBe("Item 1 renamed");
            var links = (await SendAsync(alice, HttpMethod.Get, linked))["value"]!.AsArray().ShouldHaveSingleItem()!;
            links["applicationName"]!.GetValue<string>().ShouldBe("Live; \"app\"");
            await SendAsync(alice, HttpMethod.Delete, $"{linked}/{link["id"]!.GetValue<string>()}", expected: HttpStatusCode.NoContent);
            (await SendAsync(alice, HttpMethod.Get, linked))["value"]!.AsArray().ShouldBeEmpty();

            // Deleting the task takes its subtasks with it (CalDAV alone would leave them).
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/todo/lists/{listId}/tasks/{taskId}", expected: HttpStatusCode.NoContent);
            await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/todo/lists/{listId}/tasks/{taskId}", expected: HttpStatusCode.NotFound);
            (await RawAsync(HttpMethod.Get, oneUrl)).Status.ShouldBe(HttpStatusCode.NotFound);
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/todo/lists/{listId}/tasks"))["value"]!.AsArray().ShouldBeEmpty();
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/todo/lists/{listId}", expected: HttpStatusCode.NoContent);
        }

        await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/todo/lists/{listId}", expected: HttpStatusCode.NotFound);
    }
}
