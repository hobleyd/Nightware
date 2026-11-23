using Nightware.Application.Chats;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Chats;

/// <summary>Chat messages and pins on the recorded group room <c>5opkrcg6</c>.</summary>
public sealed class TalkChatMessagesTests : IDisposable
{
    private const string Token = "5opkrcg6";
    private readonly TalkTestServer _talk = new();

    public TalkChatMessagesTests() => _talk.Reply("GET", TalkTestServer.Rooms + "/" + Token, 200, TalkTestServer.RoomData(Token));

    public void Dispose() => _talk.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_page_of_the_recorded_history()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Chat + "/" + Token, "chat-history.json", new Dictionary<string, string> { ["X-Chat-Last-Given"] = "56" });
        _talk.ReplyFixture("GET", TalkTestServer.Reaction + "/" + Token + "/59", "reactions.json");

        var page = await _talk.Messages().ListAsync(Token, new ChatPageQuery(4, null), Ct);

        page.Value.Items.Select(m => m.Id).ShouldBe(["80", "79", "78", "77"]);
        page.Value.SkipToken.ShouldBe("77");
        page.Value.Items[1].EventDetail.ShouldBeOfType<ChatEventDetail.MembersAdded>();
    }

    [Fact]
    public async Task The_deleted_message_in_a_page_carries_its_deletion_time()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Chat + "/" + Token, "chat-history.json", new Dictionary<string, string> { ["X-Chat-Last-Given"] = "56" });
        _talk.ReplyFixture("GET", TalkTestServer.Reaction + "/" + Token + "/59", "reactions.json");

        var page = await _talk.Messages().ListAsync(Token, new ChatPageQuery(8, null), Ct);

        var deleted = page.Value.Items.Single(m => m.Id == "72");
        deleted.DeletedDateTime.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1795426363));
        page.Value.Items.Single(m => m.Id == "59").Reactions.ShouldHaveSingleItem().User.Id.ShouldBe("id-bob");
    }

    [Fact]
    public async Task Messages_of_a_room_that_is_not_a_chat_are_not_found()
    {
        _talk.Reply("GET", TalkTestServer.Rooms + "/jxcmpbgy", 200, TalkTestServer.RoomData("jxcmpbgy"));

        (await _talk.Messages().ListAsync("jxcmpbgy", new ChatPageQuery(4, null), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _talk.Messages().GetAsync("jxcmpbgy", "13", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_message_is_got_by_id()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Chat + "/" + Token + "/76/context", "chat-history.json");

        (await _talk.Messages().GetAsync(Token, "76", Ct)).Value.Attachments.ShouldHaveSingleItem().Id.ShouldBe("467");
    }

    [Fact]
    public async Task A_sent_message_is_talk_markdown_and_comes_back_as_graphs()
    {
        _talk.Reply("POST", TalkTestServer.Chat + "/" + Token, 201,
            """{"id":81,"token":"5opkrcg6","actorType":"users","actorId":"alice","actorDisplayName":"Alice Anderson","timestamp":1795426800,"message":"Hi {mention-user1}","messageParameters":{"mention-user1":{"type":"user","id":"bob","name":"Bob Brown","mention-id":"bob"}},"systemMessage":"","messageType":"comment","markdown":true,"reactions":[]}""");
        var draft = new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Html, "<p>Hi <at id=\"0\">Bob</at></p>")) { Mentions = [new ChatMentionDraft(0, "Bob", "id-bob")] };

        var sent = await _talk.Messages().SendAsync(Token, draft, Ct);

        _talk.Last.RequestMessage!.Body.ShouldBe("message=Hi+%40%22bob%22");
        sent.Value.Id.ShouldBe("81");
        sent.Value.Body.Content.ShouldBe("<p>Hi <at id=\"0\">Bob Brown</at></p>");
        sent.Value.Mentions.Single().Mentioned.ShouldBe(new ChatMentioned.User(new ChatIdentity("id-bob", "Bob Brown")));
    }

    [Fact]
    public async Task Edits_deletes_and_reactions_name_the_message()
    {
        _talk.ReplyFixture("PUT", TalkTestServer.Chat + "/" + Token + "/59", "edit-answer.json");
        _talk.Reply("DELETE", TalkTestServer.Chat + "/" + Token + "/72", 200);
        _talk.Reply("POST", TalkTestServer.Reaction + "/" + Token + "/59", 201);
        _talk.Reply("DELETE", TalkTestServer.Reaction + "/" + Token + "/59", 200);
        var messages = _talk.Messages();

        (await messages.UpdateAsync(Token, "59", new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "plain")), Ct)).IsSuccess.ShouldBeTrue();
        _talk.Last.RequestMessage!.Body.ShouldBe("message=plain");
        (await messages.SoftDeleteAsync(Token, "72", Ct)).IsSuccess.ShouldBeTrue();
        (await messages.SetReactionAsync(Token, "59", "👍", Ct)).IsSuccess.ShouldBeTrue();
        (await messages.UnsetReactionAsync(Token, "59", "👍", Ct)).IsSuccess.ShouldBeTrue();

        var before = _talk.Requests.Count;
        (await messages.UpdateAsync(Token, "x", new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "plain")), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await messages.SoftDeleteAsync(Token, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await messages.SetReactionAsync(Token, "x", "👍", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await messages.UnsetReactionAsync(Token, "x", "👍", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _talk.Requests.Count.ShouldBe(before);
    }

    [Fact]
    public async Task An_edit_whose_mention_names_nobody_is_invalid()
    {
        var draft = new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Html, "<at id=\"0\">X</at>")) { Mentions = [new ChatMentionDraft(0, "X", "id-nobody")] };

        (await _talk.Messages().UpdateAsync(Token, "59", draft, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _talk.Messages().SendAsync(Token, draft, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Pins_are_listed_most_recent_first_with_their_messages()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Chat + "/" + Token + "/share", "pinned.json");
        _talk.ReplyFixture("GET", TalkTestServer.Reaction + "/" + Token + "/59", "reactions.json");

        var pins = await _talk.Pins().ListAsync(Token, Ct);

        pins.Value.Select(p => p.Id).ShouldBe(["66", "59"]);
        pins.Value[1].Message.Body.Content.ShouldBe("<p>Edited <strong>text</strong> <at id=\"0\">Bob Brown</at></p>");
    }

    [Fact]
    public async Task Pinning_answers_the_pinned_message_and_unpinning_removes_it()
    {
        _talk.Reply("POST", TalkTestServer.Chat + "/" + Token + "/66/pin", 200);
        _talk.ReplyFixture("GET", TalkTestServer.Chat + "/" + Token + "/66/context", "chat-history.json");
        _talk.Reply("DELETE", TalkTestServer.Chat + "/" + Token + "/66/pin", 200);

        (await _talk.Pins().PinAsync(Token, "66", Ct)).Value.Message.Body.Content.ShouldBe("<p>a reply</p>");
        (await _talk.Pins().UnpinAsync(Token, "66", Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.Pins().PinAsync(Token, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _talk.Pins().UnpinAsync(Token, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Pinning_is_a_moderators_action_in_talk()
    {
        _talk.Reply("POST", TalkTestServer.Chat + "/" + Token + "/66/pin", 403, """{"error":"permissions"}""");

        (await _talk.Pins().PinAsync(Token, "66", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }
}
