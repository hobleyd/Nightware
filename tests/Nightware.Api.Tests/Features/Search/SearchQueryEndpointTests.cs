using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Features.Chats;
using Nightware.Api.Features.Search;
using Nightware.Api.Tests.Support;
using Nightware.Application.Chats;
using Nightware.Application.Common.Query;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Search;

/// <summary>
/// <c>POST /search/query</c> for <c>chatMessage</c> (ADR 0009 D10) and the request checks every type shares; the
/// <c>driveItem</c> answer is tested with the drive (<c>DriveEndpointsTests</c>).
/// </summary>
public sealed class SearchQueryEndpointTests : IAsyncLifetime
{
    private static readonly ChatMessageHit Hit = new("71", new DateTimeOffset(2026, 11, 24, 9, 30, 0, TimeSpan.Zero), "...the budget is done")
    {
        ChatId = "nge9dkks",
        From = new ChatSender("Bob Brown", "bob@example.com"),
        WebUrl = "https://cloud.example.com/call/nge9dkks#message_71",
    };

    private readonly IChatSearch _search = Substitute.For<IChatSearch>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync(services => services.AddScoped(_ => _search));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static SearchQuery Text(string text) =>
        Arg.Is<SearchQuery>(q => q.Terms.Count == 1 && q.AnyOf.Count == 0 && q.Terms[0] == new SearchTerm(SearchField.Any, SearchOp.Contains, text));

    private async Task<HttpResponseMessage> PostAsync(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return await _host.AuthorizedClient().PostAsync("/v1.0/search/query", content, Ct);
    }

    [Fact]
    public async Task Chat_messages_are_found_as_microsoft_search_writes_them()
    {
        _search.SearchAsync(Text("budget"), 0, 25, Arg.Any<CancellationToken>()).Returns(new ChatSearchHits([Hit], true));

        using var response = await _host.AuthorizedClient().PostAsJsonAsync("/v1.0/search/query",
            new { requests = new[] { new { entityTypes = new[] { "chatMessage" }, query = new { queryString = "budget" }, from = 0, size = 25 } } }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body["@odata.context"]!.GetValue<string>().ShouldEndWith("$metadata#Collection(microsoft.graph.searchResponse)");
        var container = body["value"]![0]!["hitsContainers"]![0]!;
        container["total"]!.GetValue<int>().ShouldBe(1);
        container["moreResultsAvailable"]!.GetValue<bool>().ShouldBeTrue();
        var hit = container["hits"]![0]!;
        hit["hitId"]!.GetValue<string>().ShouldBe("71");
        hit["rank"]!.GetValue<int>().ShouldBe(1);
        hit["summary"]!.GetValue<string>().ShouldBe("...the budget is done");
        JsonNode.DeepEquals(hit["resource"], JsonNode.Parse("""
            {"@odata.type":"#microsoft.graph.chatMessage","id":"71","createdDateTime":"2026-11-24T09:30:00Z","lastModifiedDateTime":"2026-11-24T09:30:00Z",
             "importance":"normal","webLink":"https://cloud.example.com/call/nge9dkks#message_71",
             "from":{"emailAddress":{"name":"Bob Brown","address":"bob@example.com"}},"channelIdentity":{},"etag":"71","chatId":"nge9dkks"}
            """)).ShouldBeTrue(hit["resource"]!.ToJsonString());
    }

    [Fact]
    public async Task The_window_defaults_to_graphs_and_ranks_follow_from()
    {
        _search.SearchAsync(Text("plan"), 0, 25, Arg.Any<CancellationToken>()).Returns(new ChatSearchHits([], false));
        _search.SearchAsync(Text("plan"), 3, 2, Arg.Any<CancellationToken>()).Returns(new ChatSearchHits([Hit, Hit with { Id = "72", From = null, WebUrl = null }], false));

        using var first = await PostAsync("""{"requests":[{"entityTypes":["chatMessage"],"query":{"queryString":"plan"}}]}""");
        using var later = await PostAsync("""{"requests":[{"entityTypes":["ChatMessage"],"query":{"queryString":"plan"},"from":3,"size":2}]}""");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var hits = (await later.ReadObjectAsync())["value"]![0]!["hitsContainers"]![0]!;
        // Microsoft Search's total for Teams messages is the hits on the page.
        hits["total"]!.GetValue<int>().ShouldBe(2);
        hits["hits"]![1]!["rank"]!.GetValue<int>().ShouldBe(5);
        hits["hits"]![1]!["resource"]!.AsObject().ContainsKey("from").ShouldBeFalse();
        hits["hits"]![1]!["resource"]!.AsObject().ContainsKey("webLink").ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"requests":[{"entityTypes":["chatMessage","driveItem"],"query":{"queryString":"x"}}]}""", "notSupported")]
    [InlineData("""{"requests":[{"entityTypes":["event"],"query":{"queryString":"x"}}]}""", "notSupported")]
    [InlineData("""{"requests":[{"entityTypes":["chatMessage"],"query":{"queryString":" "}}]}""", "invalidRequest")]
    [InlineData("""{"requests":[{"entityTypes":["chatMessage"],"query":{"queryString":"x"},"from":-1}]}""", "invalidRequest")]
    [InlineData("""{"requests":[{"entityTypes":["chatMessage"],"query":{"queryString":"x"},"from":"0"}]}""", "invalidRequest")]
    [InlineData("""{"requests":[{"entityTypes":[],"query":{"queryString":"x"}}]}""", "invalidRequest")]
    [InlineData("""{"requests":[{"entityTypes":["chatMessage"],"query":{"queryString":"( )"}}]}""", "invalidRequest")]
    public async Task A_request_searched_nowhere_is_refused(string body, string code)
    {
        using var response = await PostAsync(body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe(code);
        await _search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task A_backend_failure_is_graphs_error()
    {
        _search.SearchAsync(Text("x"), 0, 25, Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Talk is down."));

        using var response = await PostAsync("""{"requests":[{"entityTypes":["chatMessage"],"query":{"queryString":"x"}}]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public void Parse_names_the_one_type_and_leaves_size_to_the_type()
    {
        var request = SearchQueryEndpoint.Parse(JsonNode.Parse("""{"requests":[{"entityTypes":["CHATMESSAGE"],"query":{"queryString":"a b"}}]}""")!.AsObject());

        request.Value.ShouldBe(new SearchRequest(ChatSearchQuery.ChatMessageType, "a b", 0, null));
        SearchQueryEndpoint.Parse(null).Error!.Target.ShouldBe("requests");
    }

    [Fact]
    public async Task The_query_string_is_read_as_kql_with_microsofts_teams_scope_terms()
    {
        SearchQuery? asked = null;
        _search.SearchAsync(Arg.Do<SearchQuery>(q => asked = q), 0, 25, Arg.Any<CancellationToken>()).Returns(new ChatSearchHits([], false));

        using var response = await PostAsync("""{"requests":[{"entityTypes":["chatMessage"],"query":{"queryString":"\"q3 budget\" from:bob sent > 2022-07-14 IsMentioned:true NOT draft"}}]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        asked!.Terms.ShouldBe([
            new SearchTerm(SearchField.Any, SearchOp.Contains, "q3 budget"),
            new SearchTerm(SearchField.From, SearchOp.Contains, "bob"),
            new SearchTerm(SearchField.Sent, SearchOp.GreaterThan, "2022-07-14"),
            new SearchTerm(SearchField.IsMentioned, SearchOp.Equals, "true"),
            new SearchTerm(SearchField.Any, SearchOp.Contains, "draft") { Negated = true },
        ]);
    }

    [Fact]
    public async Task A_channel_hit_names_its_team_and_channel_and_has_no_chat()
    {
        var channel = Hit with { ChatId = null, Channel = new ChannelIdentity("team1", "indwxaa7") };
        _search.SearchAsync(Text("budget"), 0, 25, Arg.Any<CancellationToken>()).Returns(new ChatSearchHits([channel], false));

        using var response = await PostAsync("""{"requests":[{"entityTypes":["chatMessage"],"query":{"queryString":"budget"}}]}""");

        var resource = (await response.ReadObjectAsync())["value"]![0]!["hitsContainers"]![0]!["hits"]![0]!["resource"]!;
        JsonNode.DeepEquals(resource["channelIdentity"], JsonNode.Parse("""{"teamId":"team1","channelId":"indwxaa7"}""")).ShouldBeTrue();
        resource["chatId"].ShouldBeNull();
    }

    [Fact]
    public async Task An_overlong_query_is_refused()
    {
        using var response = await PostAsync($$$"""{"requests":[{"entityTypes":["chatMessage"],"query":{"queryString":"{{{new string('x', SearchChatMessages.MaxTextLength + 1)}}}"}}]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, Ct);
    }
}
