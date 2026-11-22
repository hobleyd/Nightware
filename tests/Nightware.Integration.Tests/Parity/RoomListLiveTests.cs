using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Parity;

/// <summary>Phase 19's smaller items against the dev stack: room lists from the rooms' buildings, and translateExchangeIds.</summary>
public sealed class RoomListLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_dev_buildings_are_room_lists_holding_their_rooms_and_ids_translate_to_themselves()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var bob = await stack.GraphClientAsync("bob", Ct);

        var lists = JsonNode.Parse(await bob.GetStringAsync("/v1.0/places/microsoft.graph.roomList", Ct))!["value"]!.AsArray();
        var hq = lists.Single(l => l!["displayName"]!.GetValue<string>() == "Nightware HQ")!;
        hq["emailAddress"]!.GetValue<string>().ShouldBe("nightware-hq@roomlists.invalid");
        hq["address"]!["street"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();

        var rooms = JsonNode.Parse(await bob.GetStringAsync($"/v1.0/places/{hq["emailAddress"]!.GetValue<string>()}/microsoft.graph.roomlist/rooms", Ct))!["value"]!.AsArray();
        rooms.Count.ShouldBeGreaterThan(0);
        rooms.ShouldAllBe(r => r!["building"]!.GetValue<string>() == "Nightware HQ");

        var all = JsonNode.Parse(await bob.GetStringAsync("/v1.0/places/microsoft.graph.room", Ct))!["value"]!.AsArray();
        rooms.Count.ShouldBe(all.Count(r => r!["building"]?.GetValue<string>() == "Nightware HQ"));

        var inbox = JsonNode.Parse(await bob.GetStringAsync("/v1.0/me/mailFolders/inbox/messages?$select=id&$top=2", Ct))!["value"]!.AsArray();
        var ids = inbox.Select(m => m!["id"]!.GetValue<string>()).DefaultIfEmpty("e.none").ToArray();
        using var translated = await bob.PostAsync("/v1.0/me/translateExchangeIds",
            JsonContent.Create(new { inputIds = ids, sourceIdType = "restId", targetIdType = "restImmutableEntryId" }), Ct);
        translated.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await translated.Content.ReadAsStringAsync(Ct))!["value"]!.AsArray().Select(v => v!["targetId"]!.GetValue<string>()).ShouldBe(ids);
    }
}
