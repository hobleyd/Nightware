using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Nightware.Api.Changes;
using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Changes;

/// <summary>The change stream (ADR 0007) against the running stack: Talk's room list as the <c>chat</c> area (D5).</summary>
public sealed class ChangeStreamLiveTests : IAsyncLifetime
{
    private LiveStack? _stack;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        if (DevStack.Enabled)
        {
            _stack = LiveStack.Start(new Dictionary<string, string?> { ["ChangeStream:PollInterval"] = "00:00:02" });
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stack is not null)
        {
            await _stack.DisposeAsync();
        }
    }

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string url, string? json, HttpStatusCode expected)
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

    /// <summary>
    /// The stream's events as they arrive, read by one task: the test host aborts the response when a read is
    /// cancelled, so waits time out on the channel, never on the stream.
    /// </summary>
    private static ChannelReader<(string Name, string Data)> Events(StreamReader reader)
    {
        var events = Channel.CreateUnbounded<(string Name, string Data)>();
        _ = Task.Run(async () =>
        {
            string? name = null;
            try
            {
                while (await reader.ReadLineAsync(Ct) is { } line)
                {
                    if (line.StartsWith("event: ", StringComparison.Ordinal))
                    {
                        name = line["event: ".Length..];
                    }
                    else if (name is not null && line.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        await events.Writer.WriteAsync((name, line["data: ".Length..]), Ct);
                        name = null;
                    }
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                events.Writer.TryComplete();
            }
        }, Ct);
        return events.Reader;
    }

    /// <summary>The next event, or null when <paramref name="within"/> passes first or the stream ends.</summary>
    private static async Task<(string Name, string Data)?> NextEventAsync(ChannelReader<(string Name, string Data)> events, TimeSpan within)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(within < TimeSpan.Zero ? TimeSpan.Zero : within);
        try
        {
            return await events.WaitToReadAsync(timeout.Token) && events.TryRead(out var next) ? next : null;
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            return null;
        }
    }

    [Fact]
    public async Task A_chat_message_is_signalled_as_chat()
    {
        Assert.SkipUnless(DevStack.Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var alice = await _stack!.GraphClientAsync("alice", Ct);
        var bob = await _stack!.GraphClientAsync("bob", Ct);
        static string Id(JsonObject node) => node["id"]!.GetValue<string>();
        var aliceId = Id(await SendAsync(alice, HttpMethod.Get, $"/v1.0/users/{Uri.EscapeDataString(DevStack.Address("alice"))}", null, HttpStatusCode.OK));
        var bobId = Id(await SendAsync(alice, HttpMethod.Get, $"/v1.0/users/{Uri.EscapeDataString(DevStack.Address("bob"))}", null, HttpStatusCode.OK));
        static string Bind(string id) => $$"""{"@odata.type":"#microsoft.graph.aadUserConversationMember","roles":["owner"],"user@odata.bind":"https://graph.microsoft.com/v1.0/users('{{id}}')"}""";
        var chatId = Id(await SendAsync(alice, HttpMethod.Post, "/v1.0/chats",
            $$"""{"chatType":"oneOnOne","members":[{{Bind(aliceId)}},{{Bind(bobId)}}]}""", HttpStatusCode.Created));

        using var response = await bob.GetAsync(ChangeStreamEndpoints.Path, HttpCompletionOption.ResponseHeadersRead, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        var events = Events(reader);
        var ready = (await NextEventAsync(events, TimeSpan.FromSeconds(30))).ShouldNotBeNull("the stream opens");
        ready.Name.ShouldBe("ready");
        JsonNode.Parse(ready.Data)!["areas"]!.AsArray().Select(a => a!.GetValue<string>()).ShouldContain("chat");

        await SendAsync(alice, HttpMethod.Post, $"/v1.0/chats/{chatId}/messages", """{"body":{"contentType":"text","content":"Signal me"}}""", HttpStatusCode.Created);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var signalled = false;
        while (!signalled && DateTimeOffset.UtcNow < deadline)
        {
            var next = await NextEventAsync(events, deadline - DateTimeOffset.UtcNow);
            signalled = next is ("changed", var data) && JsonNode.Parse(data)!["areas"]!.AsArray().Any(a => a!.GetValue<string>() == "chat");
        }

        signalled.ShouldBeTrue("a message from alice is a chat signal on bob's stream");
    }
}
