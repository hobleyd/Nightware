using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.OnlineMeetings;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Calls;

/// <summary>Phase 26 against the dev stack: alice rings bob over Graph, and the call rings through Talk (ADR 0009 D7).</summary>
public sealed class CallLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(JsonObject Body, HttpResponseMessage Response)> SendAsync(HttpClient client, HttpMethod method, string path, object? body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, $"{method} {path}: {text}");
        return (text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject(), response);
    }

    [Fact]
    public async Task Alice_calls_bob_who_is_rung_and_hangs_up()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var live = TalkLive.Start();
        using var alice = await live.Stack.GraphClientAsync("alice", Ct);
        var (bob, _) = await SendAsync(alice, HttpMethod.Get, $"/v1.0/users/{DevStack.Address("bob")}", null, HttpStatusCode.OK);
        var bobId = bob["id"]!.GetValue<string>();

        var (call, placed) = await SendAsync(alice, HttpMethod.Post, "/v1.0/communications/calls", new
        {
            callbackUri = "https://bot.example.com/callback",
            targets = new[] { new { identity = new { user = new { id = bobId } } } },
            requestedModalities = new[] { "audio" },
        }, HttpStatusCode.Created);
        using (placed)
        {
            var id = call["id"]!.GetValue<string>();
            var token = call["chatInfo"]!["threadId"]!.GetValue<string>();
            try
            {
                placed.Headers.Location!.AbsoluteUri.ShouldEndWith("/communications/calls/" + id);
                call["state"]!.GetValue<string>().ShouldBe("establishing");
                call["targets"]!.AsArray().Single()!["identity"]!["user"]!["id"]!.GetValue<string>().ShouldBe(bobId);
                call["@nightware.joinWebUrl"]!.GetValue<string>().ShouldBe($"{DevStack.CloudBase}/call/{token}");

                // bob is rung: Talk's call notification for the room.
                var (_, notifications) = await live.OcsAsync("bob", HttpMethod.Get, "apps/notifications/api/v2/notifications", Ct);
                notifications.EnumerateArray().ShouldContain(n => n.GetProperty("object_type").GetString() == "call" && n.GetProperty("object_id").GetString() == token);

                var (got, _) = await SendAsync(alice, HttpMethod.Get, $"/v1.0/communications/calls/{id}", null, HttpStatusCode.OK);
                got["state"]!.GetValue<string>().ShouldBe("establishing");
                got["callbackUri"]!.GetValue<string>().ShouldBe("https://bot.example.com/callback");

                // The id is alice's alone.
                using var bobClient = await live.Stack.GraphClientAsync("bob", Ct);
                (await SendAsync(bobClient, HttpMethod.Get, $"/v1.0/communications/calls/{id}", null, HttpStatusCode.NotFound)).Response.Dispose();
            }
            finally
            {
                (await SendAsync(alice, HttpMethod.Delete, $"/v1.0/communications/calls/{id}", null, HttpStatusCode.NoContent)).Response.Dispose();
            }

            var (ended, _) = await SendAsync(alice, HttpMethod.Get, $"/v1.0/communications/calls/{id}", null, HttpStatusCode.OK);
            ended["state"]!.GetValue<string>().ShouldBe("terminated");
            var (_, room) = await live.OcsAsync("alice", HttpMethod.Get, $"apps/spreed/api/v4/room/{token}", Ct);
            room.GetProperty("hasCall").GetBoolean().ShouldBeFalse();
        }
    }
}
