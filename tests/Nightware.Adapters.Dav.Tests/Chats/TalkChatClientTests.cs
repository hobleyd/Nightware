using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Chats;

/// <summary>Talk's OCS API through the real <c>DavClient</c>: request shapes, Talk's 304 and empty-map quirks, errors.</summary>
public sealed class TalkChatClientTests : IDisposable
{
    private const string Token = "5opkrcg6";
    private readonly TalkTestServer _talk = new();

    public void Dispose() => _talk.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string? Query(string name) => TalkTestServer.Query(_talk.Last, name);

    [Fact]
    public async Task Rooms_are_listed_without_marking_the_caller_online()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Rooms, "rooms.json");

        var rooms = await _talk.Client.ListRoomsAsync(Ct);

        rooms.Value.Select(r => r.Token).ShouldContain(Token);
        Query("noStatusUpdate").ShouldBe("1");
        var group = rooms.Value.Single(r => r.Token == Token);
        group.Type.ShouldBe(TalkRoomType.Group);
        group.AttendeeId.ShouldBe(17);
        group.LastMessage.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_room_is_read_and_a_missing_or_malformed_one_is_not_found()
    {
        _talk.Reply("GET", TalkTestServer.Rooms + "/" + Token, 200, TalkTestServer.RoomData(Token));
        _talk.Reply("GET", TalkTestServer.Rooms + "/gone1234", 404, """{"error":"room"}""");

        (await _talk.Client.GetRoomAsync(Token, Ct)).Value.Name.ShouldBe("p23 probe renamed");
        (await _talk.Client.GetRoomAsync("gone1234", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        var before = _talk.Requests.Count;
        (await _talk.Client.GetRoomAsync("../etc", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _talk.Client.HistoryAsync("UPPER", null, 10, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _talk.Client.SetArchivedAsync("a", true, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _talk.Requests.Count.ShouldBe(before);
    }

    [Fact]
    public async Task A_room_is_created_with_json_participants()
    {
        _talk.Reply("POST", TalkTestServer.Rooms, 201, TalkTestServer.RoomData(Token));

        var room = await _talk.Client.CreateRoomAsync(TalkRoomType.Group, "Planning", ["bob"], Ct);

        room.Value.Token.ShouldBe(Token);
        var body = JsonNode.Parse(_talk.Last.RequestMessage!.Body!)!;
        body["roomType"]!.GetValue<int>().ShouldBe(2);
        body["roomName"]!.GetValue<string>().ShouldBe("Planning");
        body["participants"]!["users"]!.AsArray().Single()!.GetValue<string>().ShouldBe("bob");
    }

    [Fact]
    public async Task A_room_answer_without_a_token_is_unavailable()
    {
        _talk.Reply("POST", TalkTestServer.Rooms, 201, """{"type":1}""");

        (await _talk.Client.CreateRoomAsync(TalkRoomType.OneToOne, null, ["bob"], Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        JsonNode.Parse(_talk.Last.RequestMessage!.Body!)!["roomName"].ShouldBeNull();
    }

    [Fact]
    public async Task History_reads_newest_first_without_moving_the_read_marker()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Chat + "/" + Token, "chat-history.json", new Dictionary<string, string> { ["X-Chat-Last-Given"] = "56" });

        var page = await _talk.Client.HistoryAsync(Token, 99, 50, 59, Ct);

        page.Value.Messages.Count.ShouldBe(15);
        page.Value.LastGiven.ShouldBe(56);
        Query("lookIntoFuture").ShouldBe("0");
        Query("limit").ShouldBe("50");
        Query("lastKnownMessageId").ShouldBe("99");
        Query("threadId").ShouldBe("59");
        Query("setReadMarker").ShouldBe("0");
        Query("markNotificationsAsRead").ShouldBe("0");
        Query("noStatusUpdate").ShouldBe("1");
    }

    [Fact]
    public async Task History_without_the_header_reads_on_from_the_oldest_message_given()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Chat + "/" + Token, "chat-history.json");

        var page = await _talk.Client.HistoryAsync(Token, null, 500, null, Ct);

        page.Value.LastGiven.ShouldBe(56);
        Query("limit").ShouldBe("200");
        Query("lastKnownMessageId").ShouldBeNull();
    }

    [Fact]
    public async Task The_end_of_history_is_talks_304_and_an_empty_page()
    {
        _talk.NotModified("GET", TalkTestServer.Chat + "/" + Token);

        var page = await _talk.Client.HistoryAsync(Token, 56, 10, null, Ct);

        page.Value.Messages.ShouldBeEmpty();
        page.Value.LastGiven.ShouldBeNull();
    }

    [Fact]
    public async Task History_of_a_room_that_is_gone_is_not_found_and_other_errors_pass()
    {
        _talk.Reply("GET", TalkTestServer.Chat + "/gone1234", 404);
        _talk.Reply("GET", TalkTestServer.Chat + "/down1234", 503);

        (await _talk.Client.HistoryAsync("gone1234", null, 10, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _talk.Client.HistoryAsync("down1234", null, 10, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task A_message_is_read_from_its_context_and_a_nearby_one_is_not_it()
    {
        var message = TalkTestServer.RecordedMessages().Single(m => m.Id == 59);
        _talk.ReplyFixture("GET", TalkTestServer.Chat + "/" + Token + "/59/context", "chat-history.json");
        _talk.Reply("GET", TalkTestServer.Chat + "/" + Token + "/60/context", 200, """[{"id":59,"token":"5opkrcg6","actorType":"users","actorId":"alice","timestamp":1,"message":"x","messageType":"comment"}]""");
        _talk.NotModified("GET", TalkTestServer.Chat + "/" + Token + "/1/context");

        (await _talk.Client.MessageAsync(Token, 59, Ct)).Value.Text.ShouldBe(message.Text);
        Query("limit").ShouldBe("0");
        (await _talk.Client.MessageAsync(Token, 60, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _talk.Client.MessageAsync(Token, 1, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_post_sends_the_form_talk_takes()
    {
        _talk.Reply("POST", TalkTestServer.Chat + "/" + Token, 201, """{"id":81,"token":"5opkrcg6","actorType":"users","actorId":"alice","timestamp":1795426500,"message":"hi","messageType":"comment","markdown":true,"messageParameters":[]}""");

        var posted = await _talk.Client.PostAsync(Token, new TalkPost("hi **there**") { ReplyTo = 59, ThreadId = 59, ThreadTitle = "Plans" }, Ct);

        posted.Value.Id.ShouldBe(81);
        _talk.Last.RequestMessage!.Body.ShouldBe("message=hi+%2A%2Athere%2A%2A&replyTo=59&threadId=59&threadTitle=Plans");
    }

    [Fact]
    public async Task A_post_talk_refuses_keeps_its_reason()
    {
        _talk.Reply("POST", TalkTestServer.Chat + "/" + Token, 403, """{"error":"permissions"}""");

        var posted = await _talk.Client.PostAsync(Token, new TalkPost("hi"), Ct);

        posted.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        posted.Error.Message.ShouldContain("permissions");
    }

    [Fact]
    public async Task An_edit_answers_the_edited_message()
    {
        _talk.ReplyFixture("PUT", TalkTestServer.Chat + "/" + Token + "/59", "edit-answer.json");

        var edited = await _talk.Client.EditAsync(Token, 59, "Edited **text** @\"bob\"", Ct);

        edited.Value.Id.ShouldBe(59);
        edited.Value.LastEditedAt.ShouldNotBeNull();
        _talk.Last.RequestMessage!.Body.ShouldBe("message=Edited+%2A%2Atext%2A%2A+%40%22bob%22");
    }

    [Fact]
    public async Task An_edit_of_a_missing_message_is_not_found()
    {
        _talk.Reply("PUT", TalkTestServer.Chat + "/" + Token + "/5", 404, """{"error":"message"}""");
        _talk.Reply("PUT", TalkTestServer.Chat + "/" + Token + "/6", 200, "[]");

        (await _talk.Client.EditAsync(Token, 5, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _talk.Client.EditAsync(Token, 6, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Deletes_reactions_pins_and_read_state_use_talks_paths()
    {
        _talk.Reply("DELETE", TalkTestServer.Chat + "/" + Token + "/72", 200);
        _talk.Reply("POST", TalkTestServer.Reaction + "/" + Token + "/59", 201);
        _talk.Reply("DELETE", TalkTestServer.Reaction + "/" + Token + "/59", 200);
        _talk.Reply("POST", TalkTestServer.Chat + "/" + Token + "/59/pin", 200);
        _talk.Reply("DELETE", TalkTestServer.Chat + "/" + Token + "/59/pin", 200);
        _talk.Reply("POST", TalkTestServer.Chat + "/" + Token + "/read", 200);
        _talk.Reply("DELETE", TalkTestServer.Chat + "/" + Token + "/read", 200);
        _talk.Reply("POST", TalkTestServer.Rooms + "/" + Token + "/archive", 200);
        _talk.Reply("DELETE", TalkTestServer.Rooms + "/" + Token + "/archive", 200);
        _talk.Reply("PUT", TalkTestServer.Rooms + "/" + Token, 200);

        (await _talk.Client.DeleteMessageAsync(Token, 72, Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.Client.ReactAsync(Token, 59, "👍", Ct)).IsSuccess.ShouldBeTrue();
        _talk.Last.RequestMessage!.Body.ShouldBe("reaction=%F0%9F%91%8D");
        (await _talk.Client.UnreactAsync(Token, 59, "👍", Ct)).IsSuccess.ShouldBeTrue();
        Query("reaction").ShouldBe("👍");
        (await _talk.Client.PinAsync(Token, 59, Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.Client.UnpinAsync(Token, 59, Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.Client.MarkReadAsync(Token, Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.Client.MarkUnreadAsync(Token, Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.Client.SetArchivedAsync(Token, true, Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.Client.SetArchivedAsync(Token, false, Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.Client.RenameRoomAsync(Token, "New name", Ct)).IsSuccess.ShouldBeTrue();
        _talk.Last.RequestMessage!.Body.ShouldBe("roomName=New+name");
        _talk.Requests.Select(r => r.RequestMessage!.Method + " " + r.RequestMessage.Path).ShouldBe(
        [
            "DELETE /ocs/v2.php/apps/spreed/api/v1/chat/5opkrcg6/72",
            "POST /ocs/v2.php/apps/spreed/api/v1/reaction/5opkrcg6/59",
            "DELETE /ocs/v2.php/apps/spreed/api/v1/reaction/5opkrcg6/59",
            "POST /ocs/v2.php/apps/spreed/api/v1/chat/5opkrcg6/59/pin",
            "DELETE /ocs/v2.php/apps/spreed/api/v1/chat/5opkrcg6/59/pin",
            "POST /ocs/v2.php/apps/spreed/api/v1/chat/5opkrcg6/read",
            "DELETE /ocs/v2.php/apps/spreed/api/v1/chat/5opkrcg6/read",
            "POST /ocs/v2.php/apps/spreed/api/v4/room/5opkrcg6/archive",
            "DELETE /ocs/v2.php/apps/spreed/api/v4/room/5opkrcg6/archive",
            "PUT /ocs/v2.php/apps/spreed/api/v4/room/5opkrcg6",
        ]);
    }

    [Fact]
    public async Task Writes_to_a_missing_message_are_not_found()
    {
        _talk.Reply("DELETE", TalkTestServer.Chat + "/" + Token + "/5", 404);
        _talk.Reply("POST", TalkTestServer.Reaction + "/" + Token + "/5", 404);
        _talk.Reply("DELETE", TalkTestServer.Reaction + "/" + Token + "/5", 404);
        _talk.Reply("POST", TalkTestServer.Chat + "/" + Token + "/5/pin", 403, """{"error":"permissions"}""");

        (await _talk.Client.DeleteMessageAsync(Token, 5, Ct)).Error!.Message.ShouldContain("message");
        (await _talk.Client.ReactAsync(Token, 5, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _talk.Client.UnreactAsync(Token, 5, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _talk.Client.PinAsync(Token, 5, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Reactions_name_who_reacted_and_when()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Reaction + "/" + Token + "/59", "reactions.json");

        var reactions = await _talk.Client.ReactionsAsync(Token, 59, Ct);

        var reaction = reactions.Value.ShouldHaveSingleItem();
        reaction.Emoji.ShouldBe("👍");
        reaction.Actor.ShouldBe(new TalkActor("users", "bob", "Bob Brown"));
        reaction.At.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1795426351));
    }

    [Fact]
    public async Task Pinned_messages_come_as_a_map_or_as_an_empty_list()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Chat + "/" + Token + "/share", "pinned.json");
        _talk.Reply("GET", TalkTestServer.Chat + "/none1234/share", 200, "[]");
        _talk.NotModified("GET", TalkTestServer.Chat + "/nada1234/share");

        var pinned = await _talk.Client.PinnedAsync(Token, Ct);

        pinned.Value.Select(m => m.Id).ShouldBe([66, 59]);
        pinned.Value[0].PinnedAt.ShouldNotBeNull();
        Query("objectType").ShouldBe("pinned");
        (await _talk.Client.PinnedAsync("none1234", Ct)).Value.ShouldBeEmpty();
        (await _talk.Client.PinnedAsync("nada1234", Ct)).Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task Participants_are_listed_added_promoted_removed_and_left()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Rooms + "/" + Token + "/participants", "participants.json");
        _talk.Reply("POST", TalkTestServer.Rooms + "/" + Token + "/participants", 200);
        _talk.Reply("POST", TalkTestServer.Rooms + "/" + Token + "/moderators", 200);
        _talk.Reply("DELETE", TalkTestServer.Rooms + "/" + Token + "/attendees", 200);
        _talk.Reply("DELETE", TalkTestServer.Rooms + "/" + Token + "/participants/self", 200);

        var participants = await _talk.Client.ParticipantsAsync(Token, Ct);
        participants.Value.Select(p => (p.AttendeeId, p.Actor.Id, p.ParticipantType)).ShouldBe([(17L, "alice", 1), (19L, "bob", 3)]);

        (await _talk.Client.AddUserAsync(Token, "bob", Ct)).IsSuccess.ShouldBeTrue();
        _talk.Last.RequestMessage!.Body.ShouldBe("newParticipant=bob&source=users");
        (await _talk.Client.PromoteAsync(Token, 19, Ct)).IsSuccess.ShouldBeTrue();
        _talk.Last.RequestMessage!.Body.ShouldBe("attendeeId=19");
        (await _talk.Client.RemoveAttendeeAsync(Token, 19, Ct)).IsSuccess.ShouldBeTrue();
        Query("attendeeId").ShouldBe("19");
        (await _talk.Client.LeaveAsync(Token, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Unexpected_answers_are_unavailable()
    {
        _talk.Reply("GET", TalkTestServer.Rooms, 200, "{}");
        _talk.Reply("GET", TalkTestServer.Rooms + "/" + Token + "/participants", 200, "{}");
        _talk.Reply("POST", TalkTestServer.Chat + "/" + Token, 201, "[]");

        (await _talk.Client.ListRoomsAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        (await _talk.Client.ParticipantsAsync(Token, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        (await _talk.Client.PostAsync(Token, new TalkPost("x"), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }
}
