using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Calls;
using Nightware.Domain.Calls;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Calls;

/// <summary>Phase 26: delegated calls through the real composition, with the call port faked (ADR 0009 D7).</summary>
public sealed class CallsEndpointsTests : IAsyncLifetime
{
    private static readonly Call Ringing = new("sealed-id", CallState.Establishing, "nge9dkks", "https://cloud.example.com/cloud/call/nge9dkks")
    {
        Source = new CallParty("me-id", "Me Example"),
        Targets = [new CallParty("bob-id", "Bob Brown")],
        RequestedModalities = ["audio"],
        CallbackUri = "https://bot.example.com/callback",
    };

    private readonly ICallService _calls = Substitute.For<ICallService>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => _host.AuthorizedClient();

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync(services => services.AddScoped(_ => _calls));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Placing_a_call_answers_201_with_its_location_and_join_link()
    {
        _calls.PlaceAsync(Arg.Any<NewCall>(), Arg.Any<CancellationToken>()).Returns(Ringing);

        using var response = await Client.PostAsJsonAsync("/v1.0/communications/calls", new
        {
            callbackUri = "https://bot.example.com/callback",
            targets = new[] { new { identity = new { user = new { id = "bob-id", displayName = "Bob" } } } },
            requestedModalities = new[] { "audio" },
            mediaConfig = new Dictionary<string, string> { ["@odata.type"] = "#microsoft.graph.serviceHostedMediaConfig" },
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.AbsoluteUri.ShouldBe($"{TestHost.GraphBase}/communications/calls/sealed-id");
        var call = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        call["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.call");
        call["state"]!.GetValue<string>().ShouldBe("establishing");
        call["direction"]!.GetValue<string>().ShouldBe("outgoing");
        call["@nightware.joinWebUrl"]!.GetValue<string>().ShouldBe("https://cloud.example.com/cloud/call/nge9dkks");
        call["chatInfo"]!["threadId"]!.GetValue<string>().ShouldBe("nge9dkks");
        call["targets"]![0]!["identity"]!["user"]!["id"]!.GetValue<string>().ShouldBe("bob-id");
        call["source"]!["identity"]!["user"]!["displayName"]!.GetValue<string>().ShouldBe("Me Example");
        await _calls.Received().PlaceAsync(
            Arg.Is<NewCall>(c => c.Targets.Single() == "bob-id" && c.RequestedModalities.Single() == "audio" && c.CallbackUri == "https://bot.example.com/callback"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{}""", "targets")]
    [InlineData("""{"targets":[{"identity":{"user":{}}}]}""", "target")]
    [InlineData("""{"targets":[{"identity":{"user":{"id":"b"}}}],"requestedModalities":"audio"}""", "requestedModalities")]
    [InlineData("""{"targets":[{"identity":{"user":{"id":"b"}}}],"requestedModalities":[1]}""", "requestedModalities")]
    [InlineData("""{"targets":[{"identity":{"user":{"id":"b"}}}],"callbackUri":5}""", "callbackUri")]
    public async Task A_bad_call_is_400(string body, string target)
    {
        using var response = await Client.PostAsync("/v1.0/communications/calls", new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(target);
        await _calls.DidNotReceiveWithAnyArgs().PlaceAsync(default!, Ct);
    }

    [Fact]
    public async Task Modalities_default_to_audio()
    {
        _calls.PlaceAsync(Arg.Any<NewCall>(), Arg.Any<CancellationToken>()).Returns(Ringing);

        using var response = await Client.PostAsync("/v1.0/communications/calls",
            new StringContent("""{"targets":[{"identity":{"user":{"id":"bob-id"}}}],"subject":"Quick one"}""", Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await _calls.Received().PlaceAsync(Arg.Is<NewCall>(c => c.RequestedModalities.Single() == "audio" && c.CallbackUri == null && c.Subject == "Quick one"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CallState.Established, "established")]
    [InlineData(CallState.Terminated, "terminated")]
    public async Task A_call_reads_its_state(CallState state, string expected)
    {
        _calls.GetAsync("sealed-id", Arg.Any<CancellationToken>()).Returns(Ringing with { State = state });

        var call = JsonNode.Parse(await Client.GetStringAsync("/v1.0/communications/calls/sealed-id", Ct))!;

        call["state"]!.GetValue<string>().ShouldBe(expected);
        call["callbackUri"]!.GetValue<string>().ShouldBe("https://bot.example.com/callback");
    }

    [Fact]
    public async Task Hanging_up_is_204_and_someone_elses_call_404()
    {
        _calls.HangUpAsync("sealed-id", Arg.Any<CancellationToken>()).Returns(Result.Success());
        _calls.HangUpAsync("other", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));

        (await Client.DeleteAsync("/v1.0/communications/calls/sealed-id", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Client.DeleteAsync("/beta/communications/calls/other", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_the_sealing_key_calls_are_501()
    {
        _calls.GetAsync("x", Arg.Any<CancellationToken>()).Returns(Error.Unsupported("off"));

        (await Client.GetAsync("/v1.0/communications/calls/x", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
    }
}
