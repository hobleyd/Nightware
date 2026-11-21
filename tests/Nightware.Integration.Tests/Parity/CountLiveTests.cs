using System.Net;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Parity;

/// <summary>Phase 18 against the dev stack: <c>$count</c> counts every match, not the page, where it is offered.</summary>
public sealed class CountLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(HttpStatusCode Status, JsonObject Body)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Ct);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject());
    }

    /// <summary>The count with $top=1 equals the number of items a full walk of the same query returns.</summary>
    private static async Task CountsEverything(HttpClient client, string path)
    {
        var (status, first) = await GetAsync(client, path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "$count=true&$top=1");
        status.ShouldBe(HttpStatusCode.OK, first.ToJsonString());
        var count = first["@odata.count"]!.GetValue<int>();

        var seen = 0;
        string? next = path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "$top=50&$select=id";
        while (next is not null)
        {
            var (_, page) = await GetAsync(client, next);
            seen += page["value"]!.AsArray().Count;
            next = page["@odata.nextLink"]?.GetValue<string>() is { } link ? new Uri(link).PathAndQuery.Replace("/graph", string.Empty, StringComparison.Ordinal) : null;
        }

        count.ShouldBe(seen, path);
    }

    [Fact]
    public async Task Count_matches_a_full_walk_on_every_route_that_offers_it_and_is_400_elsewhere()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);

        await CountsEverything(alice, "/v1.0/me/mailFolders/inbox/messages?$filter=isRead eq false");
        await CountsEverything(alice, "/v1.0/me/mailFolders/inbox/messages");
        await CountsEverything(alice, "/v1.0/me/contacts");
        await CountsEverything(alice, "/v1.0/me/events");

        (await GetAsync(alice, "/v1.0/me/mailFolders?$count=true")).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await GetAsync(alice, "/v1.0/me/mailFolders/inbox/messages/delta?$count=true")).Status.ShouldBe(HttpStatusCode.BadRequest);
    }
}
