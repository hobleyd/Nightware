using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Chats;

/// <summary>Graph pages from Talk's history: filling a page past Talk's bookkeeping, skip tokens, reactions, encoding drafts.</summary>
public sealed class TalkMessageFeedTests : IDisposable
{
    private const string Token = "5opkrcg6";
    private static readonly TalkConversation Chat = new(Token, "Planning");
    private readonly TalkTestServer _talk = new();

    public void Dispose() => _talk.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Comment(long id, string actor = "alice", string reactions = "[]") =>
        $$"""{"id":{{id}},"token":"{{Token}}","actorType":"users","actorId":"{{actor}}","actorDisplayName":"x","timestamp":{{1795426000 + id}},"message":"m{{id}}","messageParameters":[],"systemMessage":"","messageType":"comment","markdown":true,"reactions":{{reactions}}}""";

    private static string Bookkeeping(long id) =>
        $$"""{"id":{{id}},"token":"{{Token}}","actorType":"users","actorId":"bob","timestamp":{{1795426000 + id}},"message":"👍","messageParameters":[],"systemMessage":"reaction","messageType":"system"}""";

    /// <summary>History older than <paramref name="before"/> (the newest when null).</summary>
    private void History(long? before, string lastGiven, params string[] messages)
    {
        var request = Request.Create().WithPath(TalkTestServer.Chat + "/" + Token).UsingGet();
        request = before is { } b ? request.WithParam("lastKnownMessageId", b.ToString(System.Globalization.CultureInfo.InvariantCulture)) : request.WithParam("lastKnownMessageId", WireMock.Matchers.MatchBehaviour.RejectOnMatch);
        _talk.Server.Given(request).RespondWith(Response.Create().WithStatusCode(200).WithHeader("X-Chat-Last-Given", lastGiven)
            .WithBody($$$"""{"ocs":{"meta":{"status":"ok","statuscode":200,"message":""},"data":[{{{string.Join(',', messages)}}}]}}"""));
    }

    private void End(long before) =>
        _talk.Server.Given(Request.Create().WithPath(TalkTestServer.Chat + "/" + Token).UsingGet().WithParam("lastKnownMessageId", before.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .RespondWith(Response.Create().WithStatusCode(304));

    [Fact]
    public async Task A_page_skips_bookkeeping_and_reads_on_until_it_is_full()
    {
        History(null, "17", Comment(20), Bookkeeping(19), Bookkeeping(18), Comment(17));
        History(17, "10", Bookkeeping(16), Comment(15), Comment(14), Comment(10));

        var page = await _talk.Feed.PageAsync(Chat, 3, null, null, Ct);

        page.Value.Items.Select(m => m.Id).ShouldBe(["20", "17", "15"]);
        page.Value.SkipToken.ShouldBe("15");
        page.Value.Items[0].From.ShouldBe(new ChatIdentity("id-alice", "Alice Anderson"));
    }

    [Fact]
    public async Task The_next_page_reads_below_the_skip_token()
    {
        History(15, "10", Comment(14), Comment(10));
        End(10);

        var page = await _talk.Feed.PageAsync(Chat, 3, "15", null, Ct);

        page.Value.Items.Select(m => m.Id).ShouldBe(["14", "10"]);
        page.Value.SkipToken.ShouldBeNull();
    }

    [Fact]
    public async Task A_page_that_ends_exactly_with_a_round_reads_on_next_time()
    {
        History(null, "17", Comment(20), Comment(17));

        var page = await _talk.Feed.PageAsync(Chat, 2, null, null, Ct);

        page.Value.Items.Count.ShouldBe(2);
        page.Value.SkipToken.ShouldBe("17");
    }

    [Fact]
    public async Task A_long_stretch_of_bookkeeping_returns_a_short_page_with_a_link()
    {
        History(null, "95", Bookkeeping(100), Comment(99), Bookkeeping(95));
        History(95, "90", Bookkeeping(90));
        History(90, "85", Bookkeeping(85));
        History(85, "80", Bookkeeping(80));
        History(80, "75", Bookkeeping(75));

        var page = await _talk.Feed.PageAsync(Chat, 5, null, null, Ct);

        page.Value.Items.Select(m => m.Id).ShouldBe(["99"]);
        page.Value.SkipToken.ShouldBe("75");
        _talk.Requests.Count.ShouldBe(TalkMessageFeed.MaxRounds);
    }

    [Fact]
    public async Task A_thread_is_read_by_its_id()
    {
        History(null, "17", Comment(20));
        End(17);

        await _talk.Feed.PageAsync(Chat, 5, null, 20, Ct);

        TalkTestServer.Query(_talk.Requests[0], "threadId").ShouldBe("20");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("0")]
    public async Task A_bad_skip_token_is_invalid(string skipToken)
    {
        (await _talk.Feed.PageAsync(Chat, 5, skipToken, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _talk.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Who_reacted_is_read_for_reacted_messages_only()
    {
        History(null, "17", Comment(20, reactions: """{"👍":1}"""), Comment(17));
        End(17);
        _talk.Reply("GET", TalkTestServer.Reaction + "/" + Token + "/20", 200, """{"👍":[{"actorType":"users","actorId":"bob","actorDisplayName":"Bob Brown","timestamp":1795426999}]}""");

        var page = await _talk.Feed.PageAsync(Chat, 5, null, null, Ct);

        page.Value.Items[0].Reactions.ShouldHaveSingleItem().User.Id.ShouldBe("id-bob");
        page.Value.Items[1].Reactions.ShouldBeEmpty();
        _talk.Requests.Count(r => r.RequestMessage!.Path.Contains("/reaction/", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task A_directory_that_fails_fails_the_page_and_an_unknown_person_does_not()
    {
        History(null, "17", Comment(20, actor: "carol"));
        End(17);

        (await _talk.Feed.PageAsync(Chat, 5, null, null, Ct)).Value.Items[0].From.ShouldBe(new ChatIdentity(null, "x"));

        _talk.Names.UserOfAccountAsync("carol", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("keycloak down"));
        var identities = new TalkIdentities(_talk.Names);
        var feed = new TalkMessageFeed(_talk.Client, identities);
        (await feed.PageAsync(Chat, 5, null, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task One_message_is_got_by_id_and_bookkeeping_or_a_bad_id_is_not_found()
    {
        _talk.Reply("GET", TalkTestServer.Chat + "/" + Token + "/20/context", 200, "[" + Comment(20) + "]");
        _talk.Reply("GET", TalkTestServer.Chat + "/" + Token + "/19/context", 200, "[" + Bookkeeping(19) + "]");

        (await _talk.Feed.GetAsync(Chat, "20", Ct)).Value.Body.Content.ShouldBe("<p>m20</p>");
        (await _talk.Feed.GetAsync(Chat, "19", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        var before = _talk.Requests.Count;
        (await _talk.Feed.GetAsync(Chat, "1e3", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _talk.Feed.GetAsync(Chat, "../x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _talk.Requests.Count.ShouldBe(before);
    }

    [Fact]
    public async Task A_draft_names_people_by_their_uid_and_everyone_as_all()
    {
        var draft = new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Html, "<p><at id=\"0\">Bob</at> <at id=\"1\">all</at></p>"))
        {
            Mentions = [new ChatMentionDraft(0, "Bob", "id-bob"), new ChatMentionDraft(1, "all", null, IsConversation: true)],
        };

        (await _talk.Feed.EncodeAsync(draft, Ct)).Value.ShouldBe("@\"bob\" @all");
    }

    [Fact]
    public async Task A_mention_of_nobody_or_an_empty_body_is_invalid()
    {
        var nobody = new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Html, "<at id=\"0\">X</at>")) { Mentions = [new ChatMentionDraft(0, "X", "id-nobody")] };
        (await _talk.Feed.EncodeAsync(nobody, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);

        var empty = new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Html, "<p> </p><attachment id=\"1\"></attachment>"));
        (await _talk.Feed.EncodeAsync(empty, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);

        _talk.Names.AccountNameOfAsync("id-down", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));
        var down = new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Html, "<at id=\"0\">X</at>")) { Mentions = [new ChatMentionDraft(0, "X", "id-down")] };
        (await _talk.Feed.EncodeAsync(down, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }
}
