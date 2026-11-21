using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Parity;

/// <summary>Phase 18 against the dev stack: one checklist item and one linked resource read alone.</summary>
public sealed class TodoItemLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    [Fact]
    public async Task A_checklist_item_and_a_linked_resource_read_alone_as_they_list()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var bob = await stack.GraphClientAsync("bob", Ct);
        var tag = "nw-todo-" + Guid.NewGuid().ToString("N")[..8];

        var list = Uri.EscapeDataString((await SendAsync(bob, HttpMethod.Post, "/v1.0/me/todo/lists", new { displayName = tag }, HttpStatusCode.Created))["id"]!.GetValue<string>());
        try
        {
            var task = Uri.EscapeDataString((await SendAsync(bob, HttpMethod.Post, $"/v1.0/me/todo/lists/{list}/tasks", new { title = tag }, HttpStatusCode.Created))["id"]!.GetValue<string>());
            var parts = $"/v1.0/me/todo/lists/{list}/tasks/{task}";
            var step = await SendAsync(bob, HttpMethod.Post, parts + "/checklistItems", new { displayName = "Step one" }, HttpStatusCode.Created);
            var link = await SendAsync(bob, HttpMethod.Post, parts + "/linkedResources",
                new { webUrl = "https://example.com/items/1", applicationName = "Nightware test", displayName = "Item 1" }, HttpStatusCode.Created);

            var readStep = await SendAsync(bob, HttpMethod.Get, $"{parts}/checklistItems/{Uri.EscapeDataString(step["id"]!.GetValue<string>())}");
            (readStep["displayName"]!.GetValue<string>(), readStep["isChecked"]!.GetValue<bool>()).ShouldBe(("Step one", false));
            var readLink = await SendAsync(bob, HttpMethod.Get, $"{parts}/linkedResources/{Uri.EscapeDataString(link["id"]!.GetValue<string>())}");
            readLink["webUrl"]!.GetValue<string>().ShouldBe("https://example.com/items/1");

            await SendAsync(bob, HttpMethod.Get, $"{parts}/checklistItems/not-a-step", expected: HttpStatusCode.NotFound);
        }
        finally
        {
            await SendAsync(bob, HttpMethod.Delete, $"/v1.0/me/todo/lists/{list}", expected: HttpStatusCode.NoContent);
        }
    }
}
