using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Mail.Read;

/// <summary>Phase 12 <c>mailFolders/delta</c> against the running stack: Mailbox/get, then Mailbox/changes.</summary>
public sealed class FolderDeltaLiveTests : IAsyncLifetime
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
    public async Task A_rename_and_a_delete_arrive_through_the_delta_link()
    {
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var initial = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/mailFolders/delta?$select=displayName,parentFolderId");
        initial["value"]!.AsArray().ShouldContain(f => f!["displayName"]!.GetValue<string>() == "Inbox");
        var deltaLink = initial["@odata.deltaLink"]!.GetValue<string>();

        var created = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/mailFolders/msgfolderroot/childFolders", $$"""{"displayName":"nw-delta-{{_run}}"}""", HttpStatusCode.Created);
        var id = created["id"]!.GetValue<string>();
        await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/mailFolders/{id}", $$"""{"displayName":"nw-delta-{{_run}}-renamed"}""");

        var afterRename = await SendAsync(alice, HttpMethod.Get, MailLiveHost.Local(deltaLink));
        var upsert = afterRename["value"]!.AsArray().Single(f => f!["id"]!.GetValue<string>() == id)!;
        upsert["displayName"]!.GetValue<string>().ShouldBe($"nw-delta-{_run}-renamed");
        upsert.AsObject().ContainsKey("totalItemCount").ShouldBeFalse("the first request's $select is frozen into the link");

        await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/mailFolders/{id}", expected: HttpStatusCode.NoContent);
        var afterDelete = await SendAsync(alice, HttpMethod.Get, MailLiveHost.Local(afterRename["@odata.deltaLink"]!.GetValue<string>()));
        afterDelete["value"]!.AsArray().ShouldContain(f => f!["id"]!.GetValue<string>() == id && f["@removed"] != null);
    }
}
