using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Presence;

/// <summary>
/// Phase 25 against the dev stack: bob writes his own presence over Graph and alice reads it (ADR 0009 D6). Bob is the
/// one written, as other phases' live tests put alice in calls; every test puts bob back online with no message.
/// </summary>
public sealed class PresenceLiveTests
{
    private const string UserStatus = "apps/user_status/api/v1/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, $"{method} {path}: {text}");
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    private static string Text(JsonObject presence, string name) => presence[name]!.GetValue<string>();

    /// <summary>One OCS call as <paramref name="user"/>, straight to Nextcloud.</summary>
    private static async Task<(JsonElement Data, IReadOnlyList<string> Cookies)> OcsAsync(
        LiveStack stack, string user, HttpMethod method, string path, Dictionary<string, string>? form = null, string? cookie = null)
    {
        using var request = new HttpRequestMessage(method, LiveStack.CloudBase + "/ocs/v2.php/" + path.TrimStart('/') + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "format=json")
        {
            Content = form is null ? null : new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await stack.NextcloudTokenAsync(user, Ct));
        request.Headers.Add("OCS-APIRequest", "true");
        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.Add("Cookie", cookie);
        }

        using var response = await stack.Backchannel.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue($"{method} {path}: {text}");
        // The backchannel may keep the cookies itself (then the header is gone and its container sends them back).
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.Select(static c => c.Split(';')[0]).ToList() : [];
        return (JsonElement.Parse(text).GetProperty("ocs").GetProperty("data"), cookies);
    }

    private static async Task ResetAsync(HttpClient bob)
    {
        await SendAsync(bob, HttpMethod.Post, "/v1.0/me/presence/clearUserPreferredPresence");
        await SendAsync(bob, HttpMethod.Post, "/v1.0/me/presence/setStatusMessage", new { statusMessage = new { message = new { content = string.Empty, contentType = "text" } } });
    }

    [Fact]
    public async Task Bob_sets_do_not_disturb_with_a_message_and_alice_reads_it_on_every_route()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);
        using var bob = await stack.GraphClientAsync("bob", Ct);
        var bobId = Text(await SendAsync(bob, HttpMethod.Get, "/v1.0/me"), "id");
        var aliceId = Text(await SendAsync(alice, HttpMethod.Get, "/v1.0/me"), "id");
        var message = "nw-presence " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await SendAsync(bob, HttpMethod.Post, $"/v1.0/users/{bobId}/presence/setUserPreferredPresence", new { availability = "DoNotDisturb", activity = "DoNotDisturb", expirationDuration = "PT1H" });
            var expiry = DateTimeOffset.UtcNow.AddHours(1);
            await SendAsync(bob, HttpMethod.Post, "/v1.0/me/presence/setStatusMessage", new
            {
                statusMessage = new
                {
                    message = new { content = message, contentType = "text" },
                    expiryDateTime = new { dateTime = expiry.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), timeZone = "UTC" },
                },
            });

            // Bob's own carries the expiry; alice's three routes don't, as Graph's don't for another user.
            var own = await SendAsync(bob, HttpMethod.Get, "/v1.0/me/presence");
            (Text(own, "id"), Text(own, "availability"), Text(own, "activity")).ShouldBe((bobId, "DoNotDisturb", "DoNotDisturb"));
            own["statusMessage"]!["message"]!["content"]!.GetValue<string>().ShouldBe(message);
            DateTime.Parse(own["statusMessage"]!["expiryDateTime"]!["dateTime"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)
                .ShouldBe(expiry.UtcDateTime, TimeSpan.FromSeconds(2));

            var byUser = await SendAsync(alice, HttpMethod.Get, $"/v1.0/users/{bobId}/presence");
            var byId = await SendAsync(alice, HttpMethod.Get, $"/v1.0/communications/presences/{bobId}");
            var byIds = await SendAsync(alice, HttpMethod.Post, "/v1.0/communications/getPresencesByUserId", new { ids = new[] { bobId, Guid.NewGuid().ToString(), aliceId } });
            var listed = byIds["value"]!.AsArray().Select(static p => p!.AsObject()).ToList();
            listed.Select(p => Text(p, "id")).ShouldBe([bobId, aliceId]);
            foreach (var presence in new[] { byUser, byId, listed[0] })
            {
                (Text(presence, "id"), Text(presence, "availability"), Text(presence, "activity")).ShouldBe((bobId, "DoNotDisturb", "DoNotDisturb"));
                presence["statusMessage"]!.ToJsonString().ShouldBe($$$"""{"message":{"content":"{{{message}}}","contentType":"text"}}""");
            }

            // Nextcloud's own status menu reads the same status.
            var (status, _) = await OcsAsync(stack, "bob", HttpMethod.Get, UserStatus + "user_status");
            status.GetProperty("status").GetString().ShouldBe("dnd");
            status.GetProperty("message").GetString().ShouldBe(message);

            // Only bob may set bob's presence.
            await SendAsync(alice, HttpMethod.Post, $"/v1.0/users/{bobId}/presence/setUserPreferredPresence", new { availability = "Available", activity = "Available" }, HttpStatusCode.Forbidden);

            // A meeting reads as such to alice, who sees no message id, only its icon.
            await SendAsync(bob, HttpMethod.Post, "/v1.0/me/presence/setPresence", new { sessionId = "nw-live", availability = "Busy", activity = "InAMeeting", expirationDuration = "PT10M" });
            var meeting = await SendAsync(alice, HttpMethod.Get, $"/v1.0/users/{bobId}/presence");
            (Text(meeting, "availability"), Text(meeting, "activity")).ShouldBe(("Busy", "InAMeeting"));

            await ResetAsync(bob);
            var cleared = await SendAsync(bob, HttpMethod.Get, "/v1.0/me/presence");
            (Text(cleared, "availability"), Text(cleared, "activity")).ShouldBe(("Available", "Available"));
            cleared.ContainsKey("statusMessage").ShouldBeFalse();
        }
        finally
        {
            await ResetAsync(bob);
        }
    }

    [Fact]
    public async Task Bobs_automatic_replies_are_his_out_of_office_settings()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);
        using var bob = await stack.GraphClientAsync("bob", Ct);
        var bobId = Text(await SendAsync(bob, HttpMethod.Get, "/v1.0/me"), "id");
        var before = (await SendAsync(bob, HttpMethod.Get, "/v1.0/me/mailboxSettings?$select=automaticRepliesSetting"))["automaticRepliesSetting"]!.DeepClone();
        var reply = "nw-presence away " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await SendAsync(bob, HttpMethod.Patch, "/v1.0/me/mailboxSettings", new { automaticRepliesSetting = new { status = "alwaysEnabled", internalReplyMessage = reply } });

            var own = (await SendAsync(bob, HttpMethod.Get, "/v1.0/me/presence"))["outOfOfficeSettings"]!;
            own["isOutOfOffice"]!.GetValue<bool>().ShouldBeTrue();
            own["message"]!.GetValue<string>().ShouldContain(reply);

            // Bob's mailbox is not alice's to read: she sees him in the office (ADR 0009 D6, amended).
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/users/{bobId}/presence"))["outOfOfficeSettings"]!["isOutOfOffice"]!.GetValue<bool>().ShouldBeFalse();
        }
        finally
        {
            await SendAsync(bob, HttpMethod.Patch, "/v1.0/me/mailboxSettings", new JsonObject { ["automaticRepliesSetting"] = before });
        }
    }

    [Fact]
    public async Task In_a_talk_call_bob_reads_busy_in_a_call_until_he_leaves()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);
        using var bob = await stack.GraphClientAsync("bob", Ct);
        var bobId = Text(await SendAsync(bob, HttpMethod.Get, "/v1.0/me"), "id");
        await ResetAsync(bob);

        var (room, _) = await OcsAsync(stack, "bob", HttpMethod.Post, "apps/spreed/api/v4/room",
            new() { ["roomType"] = "2", ["roomName"] = "nw-presence " + Guid.NewGuid().ToString("N")[..8] });
        var token = room.GetProperty("token").GetString()!;
        try
        {
            var (_, cookies) = await OcsAsync(stack, "bob", HttpMethod.Post, $"apps/spreed/api/v4/room/{token}/participants/active");
            var cookie = string.Join("; ", cookies);
            await OcsAsync(stack, "bob", HttpMethod.Post, $"apps/spreed/api/v4/call/{token}", new() { ["flags"] = "3" }, cookie);

            var inCall = await SendAsync(alice, HttpMethod.Get, $"/v1.0/users/{bobId}/presence");
            (Text(inCall, "availability"), Text(inCall, "activity")).ShouldBe(("Busy", "InACall"));
            inCall.ContainsKey("statusMessage").ShouldBeFalse();
            Text(await SendAsync(bob, HttpMethod.Get, "/v1.0/me/presence"), "activity").ShouldBe("InACall");

            // Talk restores the status it backed up as soon as the participant leaves the call.
            await OcsAsync(stack, "bob", HttpMethod.Delete, $"apps/spreed/api/v4/call/{token}", cookie: cookie);
            Text(await SendAsync(alice, HttpMethod.Get, $"/v1.0/users/{bobId}/presence"), "activity").ShouldNotBe("InACall");
        }
        finally
        {
            await OcsAsync(stack, "bob", HttpMethod.Delete, $"apps/spreed/api/v4/room/{token}");
            await ResetAsync(bob);
        }
    }
}
