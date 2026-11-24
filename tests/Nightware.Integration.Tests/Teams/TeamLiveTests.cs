using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Teams;

/// <summary>
/// Phase 24 against the dev stack (ADR 0009 D5): alice makes a team with bob over Graph; it is a Nextcloud team with a
/// General room and a team folder; channels, posts and replies are Talk rooms and threads, also as Talk's own clients see
/// them. The team is deleted again at the end, which removes its rooms.
/// </summary>
public sealed class TeamLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LiveStack Start() =>
        LiveStack.Start(new Dictionary<string, string?> { ["Talk:PublicBaseUrl"] = LiveStack.CloudBase + "/" });

    private static async Task<(HttpStatusCode Status, JsonObject Body, HttpResponseMessage Response)> RawAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject(), response);
    }

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (status, json, response) = await RawAsync(client, method, path, body);
        response.Dispose();
        status.ShouldBe(expected, $"{method} {path}: {json.ToJsonString()}");
        return json;
    }

    private static string Id(JsonNode? node) => node!["id"]!.GetValue<string>();

    private static async Task<string> UserIdAsync(HttpClient client, string user) =>
        Id(await SendAsync(client, HttpMethod.Get, $"/v1.0/users/{Uri.EscapeDataString(DevStack.Address(user))}"));

    private static async Task<HttpClient> TalkAsync(LiveStack stack, DevStack dev, string user)
    {
        var talk = new HttpClient(dev.Trusting(new SocketsHttpHandler { UseCookies = false }));
        talk.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await stack.NextcloudTokenAsync(user, Ct));
        talk.DefaultRequestHeaders.Add("OCS-APIRequest", "true");
        talk.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return talk;
    }

    [Fact]
    public async Task Alice_makes_a_team_with_bob_who_replies_in_Talk_and_deleting_it_removes_its_rooms()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = Start();
        using var dev = DevStack.Open();
        using var alice = await stack.GraphClientAsync("alice", Ct);
        using var bob = await stack.GraphClientAsync("bob", Ct);
        using var bobTalk = await TalkAsync(stack, dev, "bob");
        using var aliceTalk = await TalkAsync(stack, dev, "alice");
        var aliceId = await UserIdAsync(alice, "alice");
        var bobId = await UserIdAsync(alice, "bob");
        var name = "nw-team-" + Guid.NewGuid().ToString("N")[..8];

        var (status, _, created) = await RawAsync(alice, HttpMethod.Post, "/v1.0/teams", new Dictionary<string, object>
        {
            ["template@odata.bind"] = "https://graph.microsoft.com/v1.0/teamsTemplates('standard')",
            ["displayName"] = name,
            ["description"] = "Made by the live test",
            ["members"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
                    ["roles"] = Array.Empty<string>(),
                    ["user@odata.bind"] = $"https://graph.microsoft.com/v1.0/users('{bobId}')",
                },
            },
        });
        status.ShouldBe(HttpStatusCode.Accepted);
        var location = created.Headers.Location!.OriginalString;
        var teamId = created.Content.Headers.ContentLocation!.OriginalString.Split('\'')[1];
        created.Dispose();
        var deleted = false;
        try
        {
            location.ShouldBe($"/teams('{teamId}')/operations('create.{teamId}')");
            var operation = await SendAsync(alice, HttpMethod.Get, "/v1.0" + location);
            operation["status"]!.GetValue<string>().ShouldBe("succeeded");
            operation["targetResourceId"]!.GetValue<string>().ShouldBe(teamId);

            // Both see it, over Graph and in Nextcloud's Teams app.
            var team = (await SendAsync(bob, HttpMethod.Get, "/v1.0/me/joinedTeams"))["value"]!.AsArray().Single(t => Id(t) == teamId)!;
            team["displayName"]!.GetValue<string>().ShouldBe(name);
            team["description"]!.GetValue<string>().ShouldBe("Made by the live test");
            team["visibility"]!.GetValue<string>().ShouldBe("private");
            (await SendAsync(alice, HttpMethod.Get, "/v1.0/me/joinedTeams"))["value"]!.AsArray().ShouldContain(t => Id(t) == teamId);
            using (var circles = await bobTalk.GetAsync($"{LiveStack.CloudBase}/ocs/v2.php/apps/circles/circles", Ct))
            {
                (await circles.Content.ReadAsStringAsync(Ct)).ShouldContain(teamId);
            }

            await SendAsync(alice, HttpMethod.Patch, $"/v1.0/teams/{teamId}", new { description = "Renamed by the live test", visibility = "public" }, HttpStatusCode.NoContent);
            var patched = await SendAsync(bob, HttpMethod.Get, $"/v1.0/teams/{teamId}");
            patched["description"]!.GetValue<string>().ShouldBe("Renamed by the live test");
            patched["visibility"]!.GetValue<string>().ShouldBe("public");

            // Members: alice owns it, bob is a member; bob is made an owner and back.
            var members = (await SendAsync(alice, HttpMethod.Get, $"/v1.0/teams/{teamId}/members"))["value"]!.AsArray();
            members.Select(m => m!["userId"]!.GetValue<string>()).Order().ShouldBe(new[] { aliceId, bobId }.Order());
            var aliceMembership = Id(members.Single(m => m!["userId"]!.GetValue<string>() == aliceId));
            var bobMembership = Id(members.Single(m => m!["userId"]!.GetValue<string>() == bobId));
            (await SendAsync(alice, HttpMethod.Patch, $"/v1.0/teams/{teamId}/members/{bobMembership}",
                new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.aadUserConversationMember", ["roles"] = new[] { "owner" } }))["roles"]![0]!.GetValue<string>().ShouldBe("owner");
            (await SendAsync(bob, HttpMethod.Get, $"/v1.0/teams/{teamId}/members/{bobMembership}"))["roles"]!.AsArray().Count.ShouldBe(1);
            await SendAsync(alice, HttpMethod.Patch, $"/v1.0/teams/{teamId}/members/{bobMembership}", new { roles = Array.Empty<string>() });
            await SendAsync(bob, HttpMethod.Delete, $"/v1.0/teams/{teamId}/members/{aliceMembership}", expected: HttpStatusCode.BadRequest);

            // General is made with the team and is its primary channel.
            var general = await SendAsync(bob, HttpMethod.Get, $"/v1.0/teams/{teamId}/primaryChannel");
            general["displayName"]!.GetValue<string>().ShouldBe("General");
            general["membershipType"]!.GetValue<string>().ShouldBe("standard");
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/teams/{teamId}/channels/{Id(general)}", expected: HttpStatusCode.BadRequest);

            var channel = await SendAsync(alice, HttpMethod.Post, $"/v1.0/teams/{teamId}/channels", new { displayName = "Design", description = "UI work" }, HttpStatusCode.Created);
            var channelId = Id(channel);
            channel["description"]!.GetValue<string>().ShouldBe("UI work");
            var channels = (await SendAsync(bob, HttpMethod.Get, $"/v1.0/teams/{teamId}/channels"))["value"]!.AsArray();
            channels.Select(Id).ShouldBe([Id(general), channelId]);
            (await SendAsync(bob, HttpMethod.Get, $"/v1.0/teams/{teamId}/channels/{channelId}/members"))["value"]!.AsArray().Count.ShouldBe(2);

            // alice posts with a subject; bob replies from Talk's own client; the reply shows over Graph.
            var post = await SendAsync(alice, HttpMethod.Post, $"/v1.0/teams/{teamId}/channels/{channelId}/messages",
                new { subject = "Kick-off", body = new { contentType = "html", content = "<p>Agenda <b>here</b></p>" } }, HttpStatusCode.Created);
            var postId = Id(post);
            post["subject"]!.GetValue<string>().ShouldBe("Kick-off");
            post["channelIdentity"]!["teamId"]!.GetValue<string>().ShouldBe(teamId);
            post["channelIdentity"]!["channelId"]!.GetValue<string>().ShouldBe(channelId);
            var delta = await SendAsync(bob, HttpMethod.Get, $"/v1.0/teams/{teamId}/channels/{channelId}/messages/delta");
            delta["value"]!.AsArray().ShouldContain(m => Id(m) == postId);

            // bob finds the post with Microsoft Search: a channel hit, by a sender Talk filters on and a day range.
            var since = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var searched = await SendAsync(bob, HttpMethod.Post, "/v1.0/search/query", new
            {
                requests = new[] { new { entityTypes = new[] { "chatMessage" }, query = new { queryString = $"Agenda from:{DevStack.Address("alice")} sent >= {since}" } } },
            });
            var channelHit = searched["value"]![0]!["hitsContainers"]![0]!["hits"]!.AsArray().Single(h => Id(h!["resource"]!) == postId)!["resource"]!;
            channelHit["channelIdentity"]!["teamId"]!.GetValue<string>().ShouldBe(teamId);
            channelHit["channelIdentity"]!["channelId"]!.GetValue<string>().ShouldBe(channelId);
            channelHit["chatId"].ShouldBeNull();
            var deltaLink = LiveStack.Local(delta["@odata.deltaLink"]!.GetValue<string>());

            using (var reply = await bobTalk.PostAsync($"{LiveStack.CloudBase}/ocs/v2.php/apps/spreed/api/v1/chat/{channelId}",
                       new FormUrlEncodedContent(new Dictionary<string, string> { ["message"] = "Sounds good", ["threadId"] = postId }), Ct))
            {
                reply.StatusCode.ShouldBe(HttpStatusCode.Created, await reply.Content.ReadAsStringAsync(Ct));
            }

            var replies = (await SendAsync(alice, HttpMethod.Get, $"/v1.0/teams/{teamId}/channels/{channelId}/messages/{postId}/replies"))["value"]!.AsArray();
            replies.Count.ShouldBe(1);
            replies[0]!["body"]!["content"]!.GetValue<string>().ShouldBe("<p>Sounds good</p>");
            replies[0]!["replyToId"]!.GetValue<string>().ShouldBe(postId);
            replies[0]!["from"]!["user"]!["id"]!.GetValue<string>().ShouldBe(bobId);
            var talkReplyId = Id(replies[0]);
            Id(await SendAsync(bob, HttpMethod.Get, $"/v1.0/teams/{teamId}/channels/{channelId}/messages/{postId}/replies/{talkReplyId}")).ShouldBe(talkReplyId);
            var graphReply = await SendAsync(bob, HttpMethod.Post, $"/v1.0/teams/{teamId}/channels/{channelId}/messages/{postId}/replies",
                new { body = new { content = "And from Graph" } }, HttpStatusCode.Created);
            graphReply["replyToId"]!.GetValue<string>().ShouldBe(postId);
            await SendAsync(bob, HttpMethod.Post, $"/v1.0/teams/{teamId}/channels/{channelId}/messages/{postId}/replies/{Id(graphReply)}/setReaction",
                new { reactionType = "👍" }, HttpStatusCode.NoContent);

            // Posts list only posts, newest first; $expand=replies brings the thread.
            var untitled = await SendAsync(bob, HttpMethod.Post, $"/v1.0/teams/{teamId}/channels/{channelId}/messages",
                new { body = new { content = "No subject here\nsecond line" } }, HttpStatusCode.Created);
            untitled["subject"].ShouldBeNull();
            var posts = (await SendAsync(alice, HttpMethod.Get, $"/v1.0/teams/{teamId}/channels/{channelId}/messages?$top=10&$expand=replies"))["value"]!.AsArray();
            posts.Where(m => m!["messageType"]!.GetValue<string>() == "message").Select(Id).ShouldBe([Id(untitled), postId]);
            posts.Single(m => Id(m) == postId)!["replies"]!.AsArray().Count.ShouldBe(2);

            // A later edit (and rename) shows in the delta.
            await SendAsync(alice, HttpMethod.Patch, $"/v1.0/teams/{teamId}/channels/{channelId}/messages/{postId}",
                new { subject = "Kick-off (moved)", body = new { contentType = "text", content = "Agenda moved" } }, HttpStatusCode.NoContent);
            var changes = await SendAsync(bob, HttpMethod.Get, deltaLink);
            var changed = changes["value"]!.AsArray().Single(m => Id(m) == postId)!;
            changed["subject"]!.GetValue<string>().ShouldBe("Kick-off (moved)");
            changed["body"]!["content"]!.GetValue<string>().ShouldBe("<p>Agenda moved</p>");
            changed["lastEditedDateTime"].ShouldNotBeNull();
            changes["value"]!.AsArray().ShouldContain(m => Id(m) == talkReplyId);
            changes["value"]!.AsArray().ShouldContain(m => Id(m) == Id(untitled));
            (await SendAsync(bob, HttpMethod.Get, LiveStack.Local(changes["@odata.deltaLink"]!.GetValue<string>())))["value"]!.AsArray().ShouldBeEmpty();

            await SendAsync(bob, HttpMethod.Post, $"/v1.0/teams/{teamId}/channels/{channelId}/messages/{Id(untitled)}/softDelete", expected: HttpStatusCode.NoContent);
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/teams/{teamId}/channels/{channelId}/messages/{Id(untitled)}"))["deletedDateTime"].ShouldNotBeNull();
            await SendAsync(alice, HttpMethod.Get, $"/v1.0/teams/{teamId}/channels/{channelId}/messages/{talkReplyId}", expected: HttpStatusCode.NotFound);

            // The channel's folder is in the team folder, and both can list it through their drive.
            var folder = await SendAsync(alice, HttpMethod.Get, $"/v1.0/teams/{teamId}/channels/{channelId}/filesFolder");
            folder["name"]!.GetValue<string>().ShouldBe("Design");
            folder["folder"].ShouldNotBeNull();
            Id(await SendAsync(bob, HttpMethod.Get, $"/v1.0/teams/{teamId}/channels/{channelId}/filesFolder")).ShouldBe(Id(folder));
            await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/drive/items/{Id(folder)}/children");
            await SendAsync(bob, HttpMethod.Get, $"/v1.0/me/drive/items/{Id(folder)}/children");

            await SendAsync(alice, HttpMethod.Patch, $"/v1.0/teams/{teamId}/channels/{channelId}", new { displayName = "Design team" }, HttpStatusCode.NoContent);
            (await SendAsync(bob, HttpMethod.Get, $"/v1.0/teams/{teamId}/channels/{channelId}"))["displayName"]!.GetValue<string>().ShouldBe("Design team");
            var scratch = Id(await SendAsync(alice, HttpMethod.Post, $"/v1.0/teams/{teamId}/channels", new { displayName = "Scratch" }, HttpStatusCode.Created));
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/teams/{teamId}/channels/{scratch}", expected: HttpStatusCode.NoContent);
            await SendAsync(alice, HttpMethod.Get, $"/v1.0/teams/{teamId}/channels/{scratch}", expected: HttpStatusCode.NotFound);

            // Only a team id can be deleted through /groups; a directory group is not this feature's.
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/groups/{Guid.NewGuid()}", expected: HttpStatusCode.NotImplemented);
            await SendAsync(bob, HttpMethod.Delete, $"/v1.0/groups/{teamId}", expected: HttpStatusCode.Forbidden);

            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/groups/{teamId}", expected: HttpStatusCode.NoContent);
            deleted = true;
            await SendAsync(alice, HttpMethod.Get, $"/v1.0/teams/{teamId}", expected: HttpStatusCode.NotFound);
            foreach (var token in new[] { Id(general), channelId })
            {
                using var room = await aliceTalk.GetAsync($"{LiveStack.CloudBase}/ocs/v2.php/apps/spreed/api/v4/room/{token}", Ct);
                room.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }
        }
        finally
        {
            if (!deleted)
            {
                (await RawAsync(alice, HttpMethod.Delete, $"/v1.0/groups/{teamId}")).Response.Dispose();
            }
        }
    }
}
