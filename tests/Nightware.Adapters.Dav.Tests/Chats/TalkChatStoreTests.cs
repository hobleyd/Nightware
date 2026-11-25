using System.Text.Json.Nodes;

using Nightware.Application.Chats;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Chats;

/// <summary>
/// Chats from alice's recorded room list: Talk updates (changelog), note to self and the sample conversation are left
/// out, and so is the Probe Team's General channel (found through the team's resources).
/// </summary>
public sealed class TalkChatStoreTests : IDisposable
{
    private const string Group = "5opkrcg6";
    private const string OneToOne = "nge9dkks";
    private const string Channel = "indwxaa7";
    private const string Team = "Hpj1aAWPsDDAZh4UF1EwHuz86oXYdxv";
    private readonly TalkTestServer _talk = new();

    public void Dispose() => _talk.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RecordedRooms()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Rooms, "rooms.json");
        _talk.ReplyFixture("GET", TalkTestServer.Ocs + "apps/circles/circles", "circles.json");
        _talk.ReplyFixture("GET", TalkTestServer.Ocs + $"teams/{Team}/resources", "team-resources.json");
    }

    [Fact]
    public async Task Chats_are_the_callers_conversations_without_notes_samples_and_channels()
    {
        RecordedRooms();

        var page = await _talk.Store().ListAsync(new ChatPageQuery(50, null), Ct);

        page.Value.Items.Select(c => c.Id).ShouldBe([Group, "oygp6e5d", OneToOne]);
        page.Value.SkipToken.ShouldBeNull();
        var group = page.Value.Items[0];
        group.ChatType.ShouldBe(ChatType.Group);
        group.Topic.ShouldBe("p23 probe renamed");
        group.WebUrl.ShouldBe("https://cloud.example.com/call/" + Group);
        group.LastUpdatedDateTime.ShouldNotBeNull();
        group.LastMessage.ShouldBeNull();
        var oneToOne = page.Value.Items[2];
        oneToOne.ChatType.ShouldBe(ChatType.OneOnOne);
        oneToOne.Topic.ShouldBeNull();
        oneToOne.JoinWebUrl.ShouldBeNull();
    }

    [Fact]
    public async Task A_page_is_a_slice_and_its_skip_token_the_offset()
    {
        RecordedRooms();

        var first = await _talk.Store().ListAsync(new ChatPageQuery(2, null), Ct);
        var second = await _talk.Store().ListAsync(new ChatPageQuery(2, first.Value.SkipToken), Ct);

        first.Value.Items.Count.ShouldBe(2);
        first.Value.SkipToken.ShouldBe("2");
        second.Value.Items.Select(c => c.Id).ShouldBe([OneToOne]);
        second.Value.SkipToken.ShouldBeNull();
        (await _talk.Store().ListAsync(new ChatPageQuery(2, "x"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Teams_being_unavailable_lists_channels_as_chats_rather_than_failing()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Rooms, "rooms.json");
        _talk.Reply("GET", TalkTestServer.Ocs + "apps/circles/circles", 404, ocsStatus: 998);

        var page = await _talk.Store().ListAsync(new ChatPageQuery(50, null), Ct);

        page.Value.Items.Select(c => c.Id).ShouldContain(Channel);
    }

    [Fact]
    public async Task The_newest_message_is_previewed_on_request()
    {
        RecordedRooms();

        var page = await _talk.Store().ListAsync(new ChatPageQuery(50, null) { WithLastMessage = true }, Ct);

        // The group's newest message is a comment, the call room's a call's end; the one-to-one's is Talk's
        // bookkeeping (an unanswered call), so it has no preview.
        page.Value.Items[0].LastMessage!.Body.Content.ShouldBe("<p>marker check</p>");
        page.Value.Items[0].LastMessage!.From!.Id.ShouldBe("id-alice");
        page.Value.Items[1].LastMessage!.EventDetail.ShouldBeOfType<ChatEventDetail.CallEnded>();
        page.Value.Items[2].LastMessage.ShouldBeNull();
        _talk.Requests.ShouldNotContain(r => r.RequestMessage!.Path.Contains("/reaction/", StringComparison.Ordinal));

        // No preview isn't no messages: the one-to-one has been written in, its newest message is just bookkeeping.
        page.Value.Items.ShouldAllBe(c => !c.NeverWritten);
    }

    [Fact]
    public async Task A_room_whose_newest_message_is_its_creation_or_none_has_never_been_written_in()
    {
        var created = JsonNode.Parse(TalkTestServer.RoomData("jxcmpbgy"))!["lastMessage"]!;
        var room = JsonNode.Parse(TalkTestServer.RoomData(Group))!;
        room["lastMessage"] = created.DeepClone();
        _talk.Reply("GET", TalkTestServer.Rooms + "/" + Group, 200, room.ToJsonString());
        var bare = JsonNode.Parse(TalkTestServer.RoomData(OneToOne))!;
        bare["lastMessage"] = new JsonArray();
        _talk.Reply("GET", TalkTestServer.Rooms + "/" + OneToOne, 200, bare.ToJsonString());

        (await _talk.Store().GetAsync(Group, Ct)).Value.NeverWritten.ShouldBeTrue();
        (await _talk.Store().GetAsync(OneToOne, Ct)).Value.NeverWritten.ShouldBeTrue();
    }

    [Fact]
    public async Task A_chat_is_got_and_a_note_to_self_is_not_one()
    {
        _talk.Reply("GET", TalkTestServer.Rooms + "/" + Group, 200, TalkTestServer.RoomData(Group));
        _talk.Reply("GET", TalkTestServer.Rooms + "/jxcmpbgy", 200, TalkTestServer.RoomData("jxcmpbgy"));
        _talk.Reply("GET", TalkTestServer.Rooms + "/q43sgukv", 200, TalkTestServer.RoomData("q43sgukv"));

        (await _talk.Store().GetAsync(Group, Ct)).Value.Viewpoint.ShouldBe(new ChatViewpoint(false, DateTimeOffset.FromUnixTimeSeconds(1795426726)));
        (await _talk.Store().GetAsync("jxcmpbgy", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _talk.Store().GetAsync("q43sgukv", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_room_bound_to_an_event_is_a_meeting_chat_with_its_join_link()
    {
        var room = JsonNode.Parse(TalkTestServer.RoomData(Group))!;
        room["objectType"] = "event";
        room["type"] = 3;
        _talk.Reply("GET", TalkTestServer.Rooms + "/" + Group, 200, room.ToJsonString());

        var chat = await _talk.Store().GetAsync(Group, Ct);

        chat.Value.ChatType.ShouldBe(ChatType.Meeting);
        chat.Value.JoinWebUrl.ShouldBe("https://cloud.example.com/call/" + Group);
    }

    [Fact]
    public async Task Without_talks_public_url_there_are_no_links()
    {
        using var talk = new TalkTestServer(links: false);
        talk.Reply("GET", TalkTestServer.Rooms + "/" + Group, 200, TalkTestServer.RoomData(Group));

        (await talk.Store().GetAsync(Group, Ct)).Value.WebUrl.ShouldBeNull();
    }

    [Fact]
    public async Task A_one_to_one_chat_is_talks_one_to_one_room()
    {
        _talk.Reply("POST", TalkTestServer.Rooms, 200, TalkTestServer.RoomData(OneToOne));

        var chat = await _talk.Store().CreateAsync(new ChatCreation(ChatType.OneOnOne, null, [new ChatMemberDraft("id-bob", true)]), Ct);

        chat.Value.Id.ShouldBe(OneToOne);
        var body = JsonNode.Parse(_talk.Last.RequestMessage!.Body!)!;
        body["roomType"]!.GetValue<int>().ShouldBe(1);
        body["participants"]!["users"]![0]!.GetValue<string>().ShouldBe("bob");
        _talk.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_group_chat_is_named_after_its_topic_and_its_owners_are_promoted()
    {
        _talk.Reply("POST", TalkTestServer.Rooms, 201, TalkTestServer.RoomData(Group));
        _talk.ReplyFixture("GET", TalkTestServer.Rooms + "/" + Group + "/participants", "participants.json");
        _talk.Reply("POST", TalkTestServer.Rooms + "/" + Group + "/moderators", 200);

        var chat = await _talk.Store().CreateAsync(new ChatCreation(ChatType.Group, "Planning", [new ChatMemberDraft("id-bob", true)]), Ct);

        chat.Value.ChatType.ShouldBe(ChatType.Group);
        JsonNode.Parse(_talk.Requests[0].RequestMessage!.Body!)!["roomName"]!.GetValue<string>().ShouldBe("Planning");
        _talk.Last.RequestMessage!.Path.ShouldEndWith("/moderators");
        _talk.Last.RequestMessage!.Body.ShouldBe("attendeeId=19");
    }

    [Fact]
    public async Task An_untitled_group_chat_is_named_after_its_members()
    {
        _talk.Reply("POST", TalkTestServer.Rooms, 201, TalkTestServer.RoomData(Group));

        await _talk.Store().CreateAsync(new ChatCreation(ChatType.Group, null, [new ChatMemberDraft("id-bob", false), new ChatMemberDraft("alice@example.com", false)]), Ct);

        JsonNode.Parse(_talk.Last.RequestMessage!.Body!)!["roomName"]!.GetValue<string>().ShouldBe("Bob Brown, Alice Anderson");
    }

    [Fact]
    public async Task A_member_the_directory_does_not_know_is_not_found()
    {
        (await _talk.Store().CreateAsync(new ChatCreation(ChatType.Group, "x", [new ChatMemberDraft("id-nobody", false)]), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _talk.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_group_chat_is_renamed_and_a_one_to_one_chat_has_no_topic()
    {
        _talk.Reply("GET", TalkTestServer.Rooms + "/" + Group, 200, TalkTestServer.RoomData(Group));
        _talk.Reply("PUT", TalkTestServer.Rooms + "/" + Group, 200);
        _talk.Reply("GET", TalkTestServer.Rooms + "/" + OneToOne, 200, TalkTestServer.RoomData(OneToOne));

        (await _talk.Store().RenameAsync(Group, "New name", Ct)).Value.Topic.ShouldBe("New name");
        _talk.Last.RequestMessage!.Body.ShouldBe("roomName=New+name");
        (await _talk.Store().RenameAsync(OneToOne, "New name", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Hiding_and_read_state_are_talks_archive_and_read_marker()
    {
        _talk.Reply("POST", TalkTestServer.Rooms + "/" + Group + "/archive", 200);
        _talk.Reply("DELETE", TalkTestServer.Rooms + "/" + Group + "/archive", 200);
        _talk.Reply("POST", TalkTestServer.Chat + "/" + Group + "/read", 200);
        _talk.Reply("DELETE", TalkTestServer.Chat + "/" + Group + "/read", 200);
        var store = _talk.Store();

        (await store.SetHiddenAsync(Group, true, Ct)).IsSuccess.ShouldBeTrue();
        (await store.SetHiddenAsync(Group, false, Ct)).IsSuccess.ShouldBeTrue();
        (await store.MarkReadAsync(Group, Ct)).IsSuccess.ShouldBeTrue();
        (await store.MarkUnreadAsync(Group, Ct)).IsSuccess.ShouldBeTrue();
        _talk.Requests.Select(r => r.RequestMessage!.Method).ShouldBe(["POST", "DELETE", "POST", "DELETE"]);
    }
}
