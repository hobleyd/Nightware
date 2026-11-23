using Nightware.Adapters.Dav.Teams;
using Nightware.Adapters.Dav.Tests.Chats;
using Nightware.Application.Chats;
using Nightware.Application.Teams;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Teams;

/// <summary>
/// Posts and replies of the recorded General channel: thread 209 ("Kick-off (renamed)", edited) with bob's reply 212,
/// the plain top-level message 211 and bob's Talk reply to it 213 (outside threads, so a post too).
/// </summary>
public sealed class TalkChannelMessagesTests : IDisposable
{
    private const string Chat = TalkTestServer.Chat + "/" + TeamTestServer.General;
    private const string Team = TeamTestServer.TeamId;
    private const string Channel = TeamTestServer.General;
    private readonly TeamTestServer _teams = new();

    public TalkChannelMessagesTests()
    {
        _teams.RecordedTeam();
        _teams.Reply("GET", TalkTestServer.Rooms + "/" + Channel, 200, TeamTestServer.RoomData(Channel));
    }

    public void Dispose() => _teams.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChannelMessageRef Post(string id) => new(Team, Channel, id);

    private void Context(long id) => _teams.Reply("GET", $"{Chat}/{id}/context", 200, TeamTestServer.MessageData(id));

    private void History()
    {
        _teams.ReplyFixture("GET", Chat, "channel-history.json");
        _teams.NotModifiedWhen(Chat, "lastKnownMessageId", "203");
    }

    [Fact]
    public async Task Posts_are_thread_starts_and_top_level_messages_newest_first()
    {
        History();

        var page = await _teams.Messages().ListPostsAsync(Team, Channel, new ChatPageQuery(10, null), Ct);

        page.Value.Items.Select(m => m.Id).ShouldBe(["213", "211", "209", "205"]);
        page.Value.SkipToken.ShouldBeNull();
        var thread = page.Value.Items.Single(m => m.Id == "209");
        thread.Subject.ShouldBe("Kick-off (renamed)");
        thread.ChannelIdentity.ShouldBe(new ChannelIdentity(Team, Channel));
        thread.ChatId.ShouldBeNull();
        thread.ReplyToId.ShouldBeNull();
        thread.LastEditedDateTime.ShouldNotBeNull();
        page.Value.Items.Single(m => m.Id == "213").ReplyToId.ShouldBeNull();
        page.Value.Items.Single(m => m.Id == "211").Subject.ShouldBeNull();
        page.Value.Items.Single(m => m.Id == "205").MessageType.ShouldBe(ChatMessageType.SystemEventMessage);
    }

    [Fact]
    public async Task Posts_page_on_from_the_last_one_given()
    {
        History();

        var page = await _teams.Messages().ListPostsAsync(Team, Channel, new ChatPageQuery(2, null), Ct);

        page.Value.Items.Select(m => m.Id).ShouldBe(["213", "211"]);
        page.Value.SkipToken.ShouldBe("211");
    }

    [Fact]
    public async Task A_threads_replies_are_read_from_the_thread()
    {
        Context(209);
        _teams.ReplyFixtureWhen("GET", Chat, "threadId", "209", "thread-history.json");
        _teams.NotModifiedWhen(Chat, "lastKnownMessageId", "209");

        var replies = await _teams.Messages().ListRepliesAsync(Post("209"), new ChatPageQuery(10, null), Ct);

        var reply = replies.Value.Items.ShouldHaveSingleItem();
        reply.Id.ShouldBe("212");
        reply.ReplyToId.ShouldBe("209");
        reply.Subject.ShouldBeNull();
        reply.From!.Id.ShouldBe("id-bob");
        TalkTestServer.Query(_teams.To("GET", Chat).First(), "threadId").ShouldBe("209");
    }

    [Fact]
    public async Task A_post_outside_threads_has_no_replies_and_takes_none()
    {
        Context(211);

        (await _teams.Messages().ListRepliesAsync(Post("211"), new ChatPageQuery(10, null), Ct)).Value.Items.ShouldBeEmpty();
        (await _teams.Messages().ReplyAsync(Post("211"), new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "hi")), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _teams.To("POST", Chat).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reply_or_bookkeeping_is_no_post()
    {
        Context(212);
        Context(210);

        (await _teams.Messages().GetAsync(Post("212"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Messages().GetAsync(Post("210"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Messages().ListRepliesAsync(Post("212"), new ChatPageQuery(10, null), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Messages().GetAsync(Post("x"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_post_and_its_reply_are_read_by_id()
    {
        Context(209);
        Context(212);

        (await _teams.Messages().GetAsync(Post("209"), Ct)).Value.Subject.ShouldBe("Kick-off (renamed)");
        (await _teams.Messages().GetAsync(new ChannelMessageRef(Team, Channel, "209", "212"), Ct)).Value.ReplyToId.ShouldBe("209");

        // A reply is only found under its own post.
        (await _teams.Messages().GetAsync(new ChannelMessageRef(Team, Channel, "211", "212"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Messages().GetAsync(new ChannelMessageRef(Team, Channel, "209", "209"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Messages().GetAsync(new ChannelMessageRef(Team, Channel, "209", "x"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_message_the_channel_lacks_is_not_found()
    {
        _teams.Reply("GET", $"{Chat}/999/context", 200, TeamTestServer.MessageData(219));

        (await _teams.Messages().GetAsync(Post("999"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Messages().GetAsync(new ChannelMessageRef(Team, Channel, "209", "999"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Messages().GetAsync(new ChannelMessageRef(Team, "indwxaa7", "209"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Posting_starts_a_thread_titled_with_the_subject()
    {
        _teams.Reply("POST", Chat, 201, """{"id":300,"token":"xtugjbpy","actorType":"users","actorId":"alice","actorDisplayName":"Alice Anderson","timestamp":1795429900,"message":"Agenda","messageType":"comment","markdown":true,"threadId":300,"isThread":true,"threadTitle":"Plans"}""");

        var posted = await _teams.Messages().PostAsync(Team, Channel, new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "Agenda")) { Subject = " Plans " }, Ct);

        posted.Value.Subject.ShouldBe("Plans");
        posted.Value.ChannelIdentity!.ChannelId.ShouldBe(Channel);
        var body = TeamTestServer.Body(_teams.To("POST", Chat).Single());
        body.ShouldContain("threadTitle=Plans");
        body.ShouldContain("message=Agenda");
    }

    [Fact]
    public async Task A_post_without_a_subject_is_titled_with_its_first_line_and_reports_none()
    {
        _teams.Reply("POST", Chat, 201, """{"id":301,"token":"xtugjbpy","actorType":"users","actorId":"alice","timestamp":1795429900,"message":"First line\nmore","messageType":"comment","threadId":301,"isThread":true,"threadTitle":"First line"}""");

        var posted = await _teams.Messages().PostAsync(Team, Channel, new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "First line\nmore")), Ct);

        posted.Value.Subject.ShouldBeNull();
        TeamTestServer.Body(_teams.To("POST", Chat).Single()).ShouldContain("threadTitle=First+line");
    }

    [Fact]
    public async Task Posting_to_a_room_not_the_teams_sends_nothing()
    {
        (await _teams.Messages().PostAsync(Team, "indwxaa7", new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "x")), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _teams.Requests.ShouldAllBe(r => r.RequestMessage!.Method == "GET");
    }

    [Fact]
    public async Task Replying_posts_into_the_thread()
    {
        Context(209);
        _teams.Reply("POST", Chat, 201, """{"id":302,"token":"xtugjbpy","actorType":"users","actorId":"bob","timestamp":1795429900,"message":"ok","messageType":"comment","threadId":209,"isThread":true,"threadTitle":"Kick-off (renamed)"}""");

        var reply = await _teams.Messages().ReplyAsync(Post("209"), new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "ok")), Ct);

        reply.Value.ReplyToId.ShouldBe("209");
        reply.Value.Subject.ShouldBeNull();
        var body = TeamTestServer.Body(_teams.To("POST", Chat).Single());
        body.ShouldContain("threadId=209");
        body.ShouldNotContain("threadTitle");
    }

    [Fact]
    public async Task Editing_a_post_edits_it_and_renames_its_thread()
    {
        Context(209);
        _teams.Reply("PUT", $"{Chat}/209", 200, TeamTestServer.MessageData(219)[1..^1]);
        _teams.Reply("PUT", $"{Chat}/threads/209", 200, "{}");

        var updated = await _teams.Messages().UpdateAsync(Post("209"), new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "New text")) { Subject = "New title" }, Ct);

        updated.IsSuccess.ShouldBeTrue();
        TeamTestServer.Body(_teams.To("PUT", $"{Chat}/209").Single()).ShouldContain("message=New+text");
        TeamTestServer.Body(_teams.To("PUT", $"{Chat}/threads/209").Single()).ShouldContain("threadTitle=New+title");
    }

    [Fact]
    public async Task Editing_a_post_with_its_own_title_keeps_the_thread()
    {
        Context(209);
        _teams.Reply("PUT", $"{Chat}/209", 200, TeamTestServer.MessageData(219)[1..^1]);

        (await _teams.Messages().UpdateAsync(Post("209"), new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "New text")), Ct)).IsSuccess.ShouldBeTrue();
        (await _teams.Messages().UpdateAsync(Post("209"), new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "x")) { Subject = "Kick-off (renamed)" }, Ct)).IsSuccess.ShouldBeTrue();
        _teams.To("PUT", $"{Chat}/threads/209").ShouldBeEmpty();
    }

    [Fact]
    public async Task Editing_a_post_titled_by_its_first_line_retitles_it()
    {
        var message = """[{"id":301,"token":"xtugjbpy","actorType":"users","actorId":"alice","timestamp":1795429900,"message":"First line","messageType":"comment","threadId":301,"isThread":true,"threadTitle":"First line"}]""";
        _teams.Reply("GET", $"{Chat}/301/context", 200, message);
        _teams.Reply("PUT", $"{Chat}/301", 200, """{"id":303,"messageType":"system","systemMessage":"message_edited","parent":{"id":301}}""");
        _teams.Reply("PUT", $"{Chat}/threads/301", 200, "{}");

        (await _teams.Messages().UpdateAsync(Post("301"), new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "Second take")), Ct)).IsSuccess.ShouldBeTrue();
        TeamTestServer.Body(_teams.To("PUT", $"{Chat}/threads/301").Single()).ShouldContain("threadTitle=Second+take");
    }

    [Fact]
    public async Task A_post_outside_threads_has_no_subject_to_change()
    {
        Context(211);

        (await _teams.Messages().UpdateAsync(Post("211"), new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "x")) { Subject = "y" }, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _teams.To("PUT", $"{Chat}/211").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_edit_is_passed_on()
    {
        Context(212);
        _teams.Reply("PUT", $"{Chat}/212", 403, "{}");

        (await _teams.Messages().UpdateAsync(new ChannelMessageRef(Team, Channel, "209", "212"), new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "x")), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Deleting_and_reacting_reach_the_resolved_message()
    {
        Context(212);
        _teams.Reply("DELETE", $"{Chat}/212", 200, "{}");
        _teams.Reply("POST", $"{TalkTestServer.Reaction}/{Channel}/212", 201, "{}");
        _teams.Reply("DELETE", $"{TalkTestServer.Reaction}/{Channel}/212", 200, "{}");
        var reply = new ChannelMessageRef(Team, Channel, "209", "212");

        (await _teams.Messages().SoftDeleteAsync(reply, Ct)).IsSuccess.ShouldBeTrue();
        (await _teams.Messages().SetReactionAsync(reply, "👍", Ct)).IsSuccess.ShouldBeTrue();
        (await _teams.Messages().UnsetReactionAsync(reply, "👍", Ct)).IsSuccess.ShouldBeTrue();
        TalkTestServer.Query(_teams.To("DELETE", $"{TalkTestServer.Reaction}/{Channel}/212").Single(), "reaction").ShouldBe("👍");
        (await _teams.Messages().SoftDeleteAsync(new ChannelMessageRef(Team, Channel, "211", "212"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Messages().SetReactionAsync(Post("212"), "👍", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Messages().UnsetReactionAsync(Post("x"), "👍", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task The_first_delta_lists_posts_and_remembers_the_newest_message()
    {
        History();

        var page = await _teams.Messages().DeltaAsync(Team, Channel, null, 50, Ct);

        page.Value.Entries.Select(e => ((DeltaEntry<ChatMessage>.Upsert)e).Item.Id).ShouldBe(["213", "211", "209", "205"]);
        page.Value.NextPage.ShouldBeNull();
        var token = ChannelDeltaToken.Decode(page.Value.Final!.Value.Value)!;
        token.ShouldBe(new ChannelDeltaToken(Channel, 219, null));
    }

    [Fact]
    public async Task A_long_first_delta_pages_with_a_next_link()
    {
        History();

        var first = await _teams.Messages().DeltaAsync(Team, Channel, null, 2, Ct);

        first.Value.Final.ShouldBeNull();
        var token = ChannelDeltaToken.Decode(first.Value.NextPage!.Value.Value)!;
        token.ShouldBe(new ChannelDeltaToken(Channel, 219, "211"));

        _teams.ReplyFixtureWhen("GET", Chat, "lastKnownMessageId", "211", "thread-history.json");
        _teams.NotModifiedWhen(Chat, "lastKnownMessageId", "209");
        var second = await _teams.Messages().DeltaAsync(Team, Channel, first.Value.NextPage, 2, Ct);
        second.Value.Entries.Select(e => ((DeltaEntry<ChatMessage>.Upsert)e).Item.Id).ShouldBe(["209"]);
        ChannelDeltaToken.Decode(second.Value.Final!.Value.Value).ShouldBe(new ChannelDeltaToken(Channel, 219, null));
    }

    [Fact]
    public async Task Later_deltas_report_new_messages_and_what_events_are_about()
    {
        _teams.ReplyFixtureWhen("GET", Chat, "lastKnownMessageId", "211", "channel-future.json", priority: 3);
        _teams.NotModifiedWhen(Chat, "lastKnownMessageId", "219");
        Context(209);

        var page = await _teams.Messages().DeltaAsync(Team, Channel, new ChannelDeltaToken(Channel, 211, null).ToToken(), 50, Ct);

        var items = page.Value.Entries.Select(e => ((DeltaEntry<ChatMessage>.Upsert)e).Item).ToList();
        items.Select(m => m.Id).ShouldBe(["209", "212", "213"]);
        items[0].Subject.ShouldBe("Kick-off (renamed)");
        items[1].ReplyToId.ShouldBe("209");
        ChannelDeltaToken.Decode(page.Value.Final!.Value.Value).ShouldBe(new ChannelDeltaToken(Channel, 219, null));
        TalkTestServer.Query(_teams.To("GET", Chat).First(), "lookIntoFuture").ShouldBe("1");
        TalkTestServer.Query(_teams.To("GET", Chat).First(), "timeout").ShouldBe("0");
    }

    [Fact]
    public async Task A_delta_with_nothing_new_keeps_its_place()
    {
        _teams.NotModifiedWhen(Chat, "lastKnownMessageId", "219");

        var page = await _teams.Messages().DeltaAsync(Team, Channel, new ChannelDeltaToken(Channel, 219, null).ToToken(), 50, Ct);

        page.Value.Entries.ShouldBeEmpty();
        ChannelDeltaToken.Decode(page.Value.Final!.Value.Value).ShouldBe(new ChannelDeltaToken(Channel, 219, null));
    }

    [Fact]
    public async Task A_token_for_another_channel_or_garbage_is_gone()
    {
        (await _teams.Messages().DeltaAsync(Team, Channel, new ChannelDeltaToken(TeamTestServer.Design, 1, null).ToToken(), 50, Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
        (await _teams.Messages().DeltaAsync(Team, Channel, new DeltaToken("garbage!"), 50, Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
        _teams.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("eyJ2IjoyLCJyIjoieHR1Z2picHkiLCJoIjoxLCJjIjpudWxsfQ")]
    [InlineData("eyJ2IjoxLCJyIjoiLi4vIiwiaCI6MSwiYyI6bnVsbH0")]
    [InlineData("eyJ2IjoxLCJyIjoieHR1Z2picHkiLCJoIjotMSwiYyI6bnVsbH0")]
    [InlineData("eyJ2IjoxLCJyIjoieHR1Z2picHkiLCJoIjoxLCJjIjoieCJ9")]
    [InlineData("bm90IGpzb24")]
    [InlineData("")]
    public void A_token_must_be_this_version_with_a_room_and_a_cursor_id(string token) =>
        ChannelDeltaToken.Decode(token).ShouldBeNull();
}
