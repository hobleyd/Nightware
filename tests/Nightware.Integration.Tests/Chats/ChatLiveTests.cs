using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Chats;

/// <summary>
/// Phase 23 against the dev stack: alice and bob chat over Graph, and over Talk directly, which is the same conversation
/// (ADR 0009 D3–D4). Everything made here is removed again; the alice–bob one-to-one chat is Talk's own and stays.
/// </summary>
public sealed class ChatLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LiveStack Start() =>
        LiveStack.Start(new Dictionary<string, string?> { ["Talk:PublicBaseUrl"] = LiveStack.CloudBase + "/" });

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, $"{method} {path}: {text}");
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    private static string Id(JsonNode? node) => node!["id"]!.GetValue<string>();

    private static async Task<string> UserIdAsync(HttpClient client, string user) =>
        Id(await SendAsync(client, HttpMethod.Get, $"/v1.0/users/{Uri.EscapeDataString(DevStack.Address(user))}"));

    private static Dictionary<string, object> UserBind(string id) => new()
    {
        ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
        ["roles"] = new[] { "owner" },
        ["user@odata.bind"] = $"https://graph.microsoft.com/v1.0/users('{id}')",
    };

    [Fact]
    public async Task Alice_and_bob_chat_one_to_one_with_mentions_reactions_edits_and_deletes()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);
        using var bob = await stack.GraphClientAsync("bob", Ct);
        var aliceId = await UserIdAsync(alice, "alice");
        var bobId = await UserIdAsync(alice, "bob");

        // Talk returns the existing one-to-one room, as Graph returns the existing chat.
        var chat = await SendAsync(alice, HttpMethod.Post, "/v1.0/chats",
            new Dictionary<string, object> { ["chatType"] = "oneOnOne", ["members"] = new[] { UserBind(aliceId), UserBind(bobId) } }, HttpStatusCode.Created);
        chat["chatType"]!.GetValue<string>().ShouldBe("oneOnOne");
        chat["topic"].ShouldBeNull();
        var chatId = Id(chat);
        chat["webUrl"]!.GetValue<string>().ShouldBe($"{LiveStack.CloudBase}/call/{chatId}");
        Id(await SendAsync(alice, HttpMethod.Post, "/v1.0/chats",
            new Dictionary<string, object> { ["chatType"] = "oneOnOne", ["members"] = new[] { UserBind(aliceId), UserBind(bobId) } }, HttpStatusCode.Created)).ShouldBe(chatId);

        var tag = Guid.NewGuid().ToString("N")[..8];
        var sent = await SendAsync(alice, HttpMethod.Post, $"/v1.0/chats/{chatId}/messages", new
        {
            body = new { contentType = "html", content = $"<p>Hi <at id=\"0\">Bob</at>, this is <b>{tag}</b></p>" },
            mentions = new[] { new { id = 0, mentionText = "Bob", mentioned = new { user = new { id = bobId, userIdentityType = "aadUser" } } } },
        }, HttpStatusCode.Created);
        var messageId = Id(sent);
        try
        {
            sent["chatId"]!.GetValue<string>().ShouldBe(chatId);
            sent["from"]!["user"]!["id"]!.GetValue<string>().ShouldBe(aliceId);
            sent["body"]!["content"]!.GetValue<string>().ShouldBe($"<p>Hi <at id=\"0\">Bob Brown</at>, this is <strong>{tag}</strong></p>");
            sent["mentions"]![0]!["mentioned"]!["user"]!["id"]!.GetValue<string>().ShouldBe(bobId);

            // Bob sees the chat and the message, and reading leaves his unread state alone.
            var bobsChats = await SendAsync(bob, HttpMethod.Get, "/v1.0/me/chats?$expand=lastMessagePreview");
            var bobsChat = bobsChats["value"]!.AsArray().Single(c => Id(c) == chatId)!;
            bobsChat["lastMessagePreview"]!["id"]!.GetValue<string>().ShouldBe(messageId);
            var listed = await SendAsync(bob, HttpMethod.Get, $"/v1.0/chats/{chatId}/messages?$top=5");
            Id(listed["value"]![0]).ShouldBe(messageId);
            (await SendAsync(bob, HttpMethod.Get, $"/v1.0/chats/{chatId}"))["viewpoint"]!["lastMessageReadDateTime"].ShouldBeNull();

            await SendAsync(bob, HttpMethod.Post, $"/v1.0/chats/{chatId}/messages/{messageId}/setReaction", new { reactionType = "like" }, HttpStatusCode.NoContent);
            var reacted = await SendAsync(alice, HttpMethod.Get, $"/v1.0/chats/{chatId}/messages/{messageId}");
            reacted["reactions"]![0]!["reactionType"]!.GetValue<string>().ShouldBe("👍");
            reacted["reactions"]![0]!["user"]!["user"]!["id"]!.GetValue<string>().ShouldBe(bobId);
            await SendAsync(bob, HttpMethod.Post, $"/v1.0/chats/{chatId}/messages/{messageId}/unsetReaction", new { reactionType = "👍" }, HttpStatusCode.NoContent);

            await SendAsync(alice, HttpMethod.Patch, $"/v1.0/chats/{chatId}/messages/{messageId}",
                new { body = new { contentType = "text", content = $"edited {tag}" } }, HttpStatusCode.NoContent);
            var edited = await SendAsync(bob, HttpMethod.Get, $"/v1.0/chats/{chatId}/messages/{messageId}");
            edited["body"]!["content"]!.GetValue<string>().ShouldBe($"<p>edited {tag}</p>");
            edited["lastEditedDateTime"].ShouldNotBeNull();
            edited["reactions"]!.AsArray().ShouldBeEmpty();

            // Paging: two more, then pages of two, newest first.
            var more = new List<string>();
            for (var i = 0; i < 2; i++)
            {
                more.Add(Id(await SendAsync(alice, HttpMethod.Post, $"/v1.0/chats/{chatId}/messages", new { body = new { content = $"page {i} {tag}" } }, HttpStatusCode.Created)));
            }

            var first = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/chats/{chatId}/messages?$top=2");
            first["value"]!.AsArray().Select(Id).ShouldBe([more[1], more[0]]);
            var second = await SendAsync(alice, HttpMethod.Get, LiveStack.Local(first["@odata.nextLink"]!.GetValue<string>()));
            Id(second["value"]![0]).ShouldBe(messageId);
            foreach (var id in more)
            {
                await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/chats/{chatId}/messages/{id}/softDelete", expected: HttpStatusCode.NoContent);
            }
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/chats/{chatId}/messages/{messageId}/softDelete", expected: HttpStatusCode.NoContent);
        }

        var deleted = await SendAsync(bob, HttpMethod.Get, $"/v1.0/chats/{chatId}/messages/{messageId}");
        deleted["deletedDateTime"].ShouldNotBeNull();
        deleted["body"]!["content"]!.GetValue<string>().ShouldBeEmpty();
        await SendAsync(bob, HttpMethod.Get, $"/v1.0/chats/{chatId}/messages/999999999", expected: HttpStatusCode.NotFound);
        await SendAsync(bob, HttpMethod.Get, $"/v1.0/users/{Uri.EscapeDataString(DevStack.Address("alice"))}/chats", expected: HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_group_chat_made_in_Talk_is_a_graph_chat_with_members_pins_and_read_state()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = Start();
        using var dev = DevStack.Open();
        using var alice = await stack.GraphClientAsync("alice", Ct);
        using var bob = await stack.GraphClientAsync("bob", Ct);
        var aliceId = await UserIdAsync(alice, "alice");
        var bobId = await UserIdAsync(alice, "bob");

        // Made in Talk itself, as its web client does (cookie-free: a bearer with a session cookie is refused).
        using var talk = new HttpClient(dev.Trusting(new SocketsHttpHandler { UseCookies = false }));
        talk.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await stack.NextcloudTokenAsync("alice", Ct));
        talk.DefaultRequestHeaders.Add("OCS-APIRequest", "true");
        var topic = "nw-chat-" + Guid.NewGuid().ToString("N")[..8];
        using var created = await talk.PostAsJsonAsync($"{LiveStack.CloudBase}/ocs/v2.php/apps/spreed/api/v4/room?format=json",
            new { roomType = 2, roomName = topic, participants = new { users = new[] { "bob" } } }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var chatId = (await created.Content.ReadFromJsonAsync<JsonObject>(Ct))!["ocs"]!["data"]!["token"]!.GetValue<string>();
        try
        {
            var listed = await SendAsync(bob, HttpMethod.Get, "/v1.0/me/chats?$expand=members");
            var chat = listed["value"]!.AsArray().Single(c => Id(c) == chatId)!;
            chat["chatType"]!.GetValue<string>().ShouldBe("group");
            chat["topic"]!.GetValue<string>().ShouldBe(topic);
            var members = chat["members"]!.AsArray();
            members.Select(m => m!["userId"]!.GetValue<string>()).Order().ShouldBe(new[] { aliceId, bobId }.Order());
            members.Single(m => m!["userId"]!.GetValue<string>() == aliceId)!["roles"]!.AsArray().Single()!.GetValue<string>().ShouldBe("owner");
            var bobsMembership = Id(members.Single(m => m!["userId"]!.GetValue<string>() == bobId));
            Id(await SendAsync(alice, HttpMethod.Get, $"/v1.0/chats/{chatId}/members/{bobsMembership}")).ShouldBe(bobsMembership);

            var history = await SendAsync(alice, HttpMethod.Get, $"/v1.0/chats/{chatId}/messages");
            history["value"]!.AsArray().ShouldContain(m => m!["eventDetail"]!["@odata.type"]!.GetValue<string>() == "#microsoft.graph.membersAddedEventMessageDetail");

            var renamed = await SendAsync(alice, HttpMethod.Patch, $"/v1.0/chats/{chatId}", new { topic = topic + " renamed" });
            renamed["topic"]!.GetValue<string>().ShouldBe(topic + " renamed");
            var renameEvent = (await SendAsync(alice, HttpMethod.Get, $"/v1.0/chats/{chatId}/messages?$top=1"))["value"]![0]!;
            renameEvent["eventDetail"]!["chatDisplayName"]!.GetValue<string>().ShouldBe(topic + " renamed");

            var message = Id(await SendAsync(bob, HttpMethod.Post, $"/v1.0/chats/{chatId}/messages", new { body = new { content = "pin me" } }, HttpStatusCode.Created));
            var pinned = await SendAsync(alice, HttpMethod.Post, $"/v1.0/chats/{chatId}/pinnedMessages",
                new Dictionary<string, string> { ["message@odata.bind"] = $"https://graph.microsoft.com/v1.0/chats/{chatId}/messages/{message}" }, HttpStatusCode.Created);
            Id(pinned).ShouldBe(message);
            var pins = await SendAsync(bob, HttpMethod.Get, $"/v1.0/chats/{chatId}/pinnedMessages?$expand=message");
            pins["value"]![0]!["message"]!["body"]!["content"]!.GetValue<string>().ShouldBe("<p>pin me</p>");
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/chats/{chatId}/pinnedMessages/{message}", expected: HttpStatusCode.NoContent);
            (await SendAsync(bob, HttpMethod.Get, $"/v1.0/chats/{chatId}/pinnedMessages"))["value"]!.AsArray().ShouldBeEmpty();

            await SendAsync(bob, HttpMethod.Post, $"/v1.0/chats/{chatId}/markChatReadForUser", new { user = new { id = bobId } }, HttpStatusCode.NoContent);
            (await SendAsync(bob, HttpMethod.Get, $"/v1.0/chats/{chatId}"))["viewpoint"]!["lastMessageReadDateTime"].ShouldNotBeNull();
            await SendAsync(bob, HttpMethod.Post, $"/v1.0/chats/{chatId}/markChatUnreadForUser", new { user = new { id = bobId } }, HttpStatusCode.NoContent);
            (await SendAsync(bob, HttpMethod.Get, $"/v1.0/chats/{chatId}"))["viewpoint"]!["lastMessageReadDateTime"].ShouldBeNull();
            await SendAsync(bob, HttpMethod.Post, $"/v1.0/chats/{chatId}/hideForUser", new { user = new { id = aliceId } }, HttpStatusCode.Forbidden);
            await SendAsync(bob, HttpMethod.Post, $"/v1.0/chats/{chatId}/hideForUser", new { user = new { id = bobId } }, HttpStatusCode.NoContent);
            (await SendAsync(bob, HttpMethod.Get, $"/v1.0/chats/{chatId}"))["viewpoint"]!["isHidden"]!.GetValue<bool>().ShouldBeTrue();
            await SendAsync(bob, HttpMethod.Post, $"/v1.0/chats/{chatId}/unhideForUser", new { user = new { id = bobId } }, HttpStatusCode.NoContent);

            // Members: bob leaves, alice adds him back (a new membership), then removes him.
            await SendAsync(bob, HttpMethod.Delete, $"/v1.0/chats/{chatId}/members/{bobsMembership}", expected: HttpStatusCode.NoContent);
            await SendAsync(bob, HttpMethod.Get, $"/v1.0/chats/{chatId}", expected: HttpStatusCode.NotFound);
            // Graph answers an added member with its Location only.
            using var add = new HttpRequestMessage(HttpMethod.Post, $"/v1.0/chats/{chatId}/members")
            {
                Content = JsonContent.Create(new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.aadUserConversationMember", ["roles"] = Array.Empty<string>(), ["user@odata.bind"] = $"https://graph.microsoft.com/v1.0/users('{bobId}')" }),
            };
            using var added = await alice.SendAsync(add, Ct);
            added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync(Ct));
            var readded = added.Headers.Location!.OriginalString.Split('/')[^1];
            readded.ShouldNotBe(bobsMembership);
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/chats/{chatId}/members/{readded}"))["userId"]!.GetValue<string>().ShouldBe(bobId);
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/chats/{chatId}/members/{readded}", expected: HttpStatusCode.NoContent);
        }
        finally
        {
            using var removed = await talk.DeleteAsync($"{LiveStack.CloudBase}/ocs/v2.php/apps/spreed/api/v4/room/{chatId}?format=json", Ct);
        }
    }

    [Fact]
    public async Task A_group_chat_made_over_graph_has_its_topic_and_owners()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);
        var aliceId = await UserIdAsync(alice, "alice");
        var bobId = await UserIdAsync(alice, "bob");

        var topic = "nw-group-" + Guid.NewGuid().ToString("N")[..8];
        var chat = await SendAsync(alice, HttpMethod.Post, "/v1.0/chats",
            new Dictionary<string, object> { ["chatType"] = "group", ["topic"] = topic, ["members"] = new[] { UserBind(aliceId), UserBind(bobId) } }, HttpStatusCode.Created);
        var chatId = Id(chat);
        try
        {
            chat["topic"]!.GetValue<string>().ShouldBe(topic);
            var members = await SendAsync(alice, HttpMethod.Get, $"/v1.0/chats/{chatId}/members");
            members["value"]!.AsArray().ShouldAllBe(m => m!["roles"]!.AsArray().Count == 1);
        }
        finally
        {
            // The caller leaving last closes the room in Talk.
            var self = (await SendAsync(alice, HttpMethod.Get, $"/v1.0/chats/{chatId}/members"))["value"]!.AsArray();
            foreach (var member in self.OrderBy(m => m!["userId"]!.GetValue<string>() == aliceId))
            {
                await SendAsync(alice, HttpMethod.Delete, $"/v1.0/chats/{chatId}/members/{Id(member)}", expected: HttpStatusCode.NoContent);
            }
        }
    }
}
