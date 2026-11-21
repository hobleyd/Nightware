using System.Net;
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

    [Fact]
    public async Task A_list_of_its_own_holds_a_task_with_an_attachment_until_all_are_deleted()
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

            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/todo/lists/{listId}/tasks/{taskId}", expected: HttpStatusCode.NoContent);
            await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/todo/lists/{listId}/tasks/{taskId}", expected: HttpStatusCode.NotFound);
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/todo/lists/{listId}", expected: HttpStatusCode.NoContent);
        }

        await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/todo/lists/{listId}", expected: HttpStatusCode.NotFound);
    }
}
