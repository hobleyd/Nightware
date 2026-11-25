using System.Text;
using System.Text.Json;

using Nightware.Adapters.Dav.Chats;
using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Adapters.Dav.Tests.Chats;
using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Adapters.Dav.Tests.Teams;

/// <summary>
/// Change notifications over Talk (ADR 0009 D11), against the recorded team (General and Design are its channels) and
/// alice's recorded rooms; new messages and Talk's events about older ones are constructed in Talk 25's shape.
/// </summary>
public sealed class TalkChangeFeedTests : IDisposable
{
    private const string OneToOne = "nge9dkks";
    private const string Group = "oygp6e5d";
    private const string Team = TeamTestServer.TeamId;
    private const string General = TeamTestServer.General;
    private static readonly MailboxRef Me = new("alice@example.com");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_795_600_000);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly TeamTestServer _teams = new();
    private readonly TimeProvider _time = Substitute.For<TimeProvider>();

    public TalkChangeFeedTests()
    {
        _time.GetUtcNow().Returns(Now);
        _teams.RecordedTeam();
    }

    public void Dispose() => _teams.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TalkChangeFeed Feed() =>
        new(_teams.Talk.Client, _teams.Talk.ChatRooms, _teams.Talk.Channels, _teams.Channels, _teams.Talk.Members(), _time);

    private static SubscriptionTarget Target(SubscriptionResourceKind kind, string? folder = null) => new(Me, kind, folder);

    private static string Chat(string token) => TalkTestServer.Chat + "/" + token;

    private static string State(object state) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, Web)));

    private static T Read<T>(string state) => JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(Convert.FromBase64String(state)), Web)!;

    private static string Message(long id, string token, string type = "comment", string system = "", string? parent = null, long timestamp = 1_795_500_000) =>
        $$"""{"id":{{id}},"token":"{{token}}","actorType":"users","actorId":"bob","actorDisplayName":"Bob Brown","timestamp":{{timestamp}},"messageType":"{{type}}","systemMessage":"{{system}}","message":"m{{id}}","messageParameters":[]{{(parent is null ? "" : ",\"parent\":" + parent)}}}""";

    private void Future(string token, long after, params string[] messages)
    {
        _teams.Talk.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(Chat(token)).UsingGet().WithParam("lastKnownMessageId", after.ToString(System.Globalization.CultureInfo.InvariantCulture)).WithParam("lookIntoFuture", "1"))
            .AtPriority(2)
            .RespondWith(messages.Length == 0
                ? WireMock.ResponseBuilders.Response.Create().WithStatusCode(304)
                : WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                    .WithBody("""{"ocs":{"meta":{"status":"ok","statuscode":200,"message":""},"data":[""" + string.Join(',', messages) + "]}}"));
    }

    private void Newest(string token, params string[] messages) =>
        _teams.Talk.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(Chat(token)).UsingGet().WithParam("lookIntoFuture", "0"))
            .AtPriority(3)
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"ocs":{"meta":{"status":"ok","statuscode":200,"message":""},"data":[""" + string.Join(',', messages) + "]}}"));

    private void Room(string token) => _teams.Reply("GET", TalkTestServer.Rooms + "/" + token, 200, TeamTestServer.RoomData(token));

    [Fact]
    public async Task Channel_posts_and_replies_are_created_and_events_update_their_post()
    {
        _teams.ReplyFixtureWhen("GET", Chat(General), "lastKnownMessageId", "211", "channel-future.json", priority: 3);
        _teams.NotModifiedWhen(Chat(General), "lastKnownMessageId", "219");

        var batch = await Feed().ChangesAsync(Target(SubscriptionResourceKind.ChannelMessages, $"{Team}/{General}"), State(new { cursor = 211 }), Ct);

        var channel = $"teams('{Team}')/channels('{General}')/messages";
        batch.Value.Classified.ShouldBeTrue();
        batch.Value.Changes.Select(c => (c.Id, c.ChangeType, c.Resource)).ShouldBe([
            ("212", SubscriptionChangeType.Created, $"{channel}('209')/replies('212')"),
            ("213", SubscriptionChangeType.Created, $"{channel}('213')"),
            ("209", SubscriptionChangeType.Updated, $"{channel}('209')"),
        ]);
        batch.Value.Changes.ShouldAllBe(c => c.ODataType == "#Microsoft.Graph.chatMessage");
        Read<TalkChangeFeed.CursorState>(batch.Value.State).Cursor.ShouldBe(219);
        var call = _teams.To("GET", Chat(General)).First();
        TalkTestServer.Query(call, "setReadMarker").ShouldBe("0");
        TalkTestServer.Query(call, "markNotificationsAsRead").ShouldBe("0");
        TalkTestServer.Query(call, "timeout").ShouldBe("0");
    }

    [Fact]
    public async Task Chat_edits_and_deletes_name_their_message_and_a_new_messages_edit_is_not_an_update()
    {
        Future(OneToOne, 180,
            Message(181, OneToOne),
            Message(182, OneToOne, "system", "message_edited", Message(170, OneToOne)),
            Message(183, OneToOne, "system", "message_deleted", Message(175, OneToOne, "comment_deleted")),
            Message(184, OneToOne, "system", "message_edited", Message(181, OneToOne)),
            Message(185, OneToOne, "system", "reaction", Message(170, OneToOne)));
        Future(OneToOne, 185);

        var batch = await Feed().ChangesAsync(Target(SubscriptionResourceKind.ChatMessages, OneToOne), State(new { cursor = 180 }), Ct);

        batch.Value.Changes.Select(c => (c.Id, c.ChangeType, c.Resource)).ShouldBe([
            ("181", SubscriptionChangeType.Created, $"chats('{OneToOne}')/messages('181')"),
            ("170", SubscriptionChangeType.Updated, $"chats('{OneToOne}')/messages('170')"),
            ("175", SubscriptionChangeType.Deleted, $"chats('{OneToOne}')/messages('175')"),
        ]);
        Read<TalkChangeFeed.CursorState>(batch.Value.State).Cursor.ShouldBe(185);
    }

    [Fact]
    public async Task A_chat_subscription_starts_at_the_newest_message_and_a_channel_is_not_a_chat()
    {
        Room(OneToOne);
        Room(General);
        Newest(OneToOne, Message(190, OneToOne, "system", "message_edited"));

        var started = await Feed().StartAsync(Target(SubscriptionResourceKind.ChatMessages, OneToOne), Ct);
        var channel = await Feed().StartAsync(Target(SubscriptionResourceKind.ChatMessages, General), Ct);
        var unknown = await Feed().StartAsync(Target(SubscriptionResourceKind.Chat, "zzzz9999"), Ct);

        Read<TalkChangeFeed.CursorState>(started.Value).Cursor.ShouldBe(190);
        channel.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        unknown.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task All_messages_follow_each_chat_and_a_chat_that_appears_reports_what_was_sent_since_the_last_poll()
    {
        var at = Now.AddMinutes(-1).ToUnixTimeSeconds();
        Future(OneToOne, 180, Message(181, OneToOne));
        Future(OneToOne, 181);
        Newest(Group, Message(50, Group, timestamp: at + 10), Message(40, Group, timestamp: at - 100));

        var batch = await Feed().ChangesAsync(
            Target(SubscriptionResourceKind.AllChatMessages),
            State(new { rooms = new Dictionary<string, long> { [OneToOne] = 180, ["gone1234"] = 5 }, channels = new[] { General, TeamTestServer.Design }, at }),
            Ct);

        batch.Value.Changes.Select(c => c.Resource).ShouldBe([$"chats('{OneToOne}')/messages('181')", $"chats('{Group}')/messages('50')"], ignoreOrder: true);
        var state = Read<TalkChangeFeed.AllChatsState>(batch.Value.State);
        state.Rooms[OneToOne].ShouldBe(181);
        state.Rooms[Group].ShouldBe(50);
        state.Rooms.ShouldNotContainKey("gone1234");
        state.Rooms.ShouldNotContainKey(General);
        state.At.ShouldBe(Now.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Chats_that_appear_are_created_and_a_renamed_group_is_updated()
    {
        var start = await Feed().StartAsync(Target(SubscriptionResourceKind.Chats), Ct);
        var known = Read<TalkChangeFeed.ChatsState>(start.Value);
        known.Topics.ShouldContainKey(OneToOne);
        known.Topics.ShouldNotContainKey(General);
        known.Topics.ShouldNotContainKey("jxcmpbgy");
        var topics = new Dictionary<string, string>(known.Topics);
        topics.Remove(OneToOne);
        topics[Group] = "old name";

        var batch = await Feed().ChangesAsync(Target(SubscriptionResourceKind.Chats), State(new { topics, channels = known.Channels }), Ct);

        batch.Value.Changes.Select(c => (c.Id, c.ChangeType, c.Resource, c.ODataType)).ShouldBe([
            (Group, SubscriptionChangeType.Updated, $"chats('{Group}')", "#microsoft.graph.chat"),
            (OneToOne, SubscriptionChangeType.Created, $"chats('{OneToOne}')", "#microsoft.graph.chat"),
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task Members_who_join_are_created_and_those_who_leave_deleted()
    {
        Room(OneToOne);
        _teams.Talk.ReplyFixture("GET", TalkTestServer.Rooms + "/" + OneToOne + "/participants", "participants.json");
        var alice = TalkIds.MembershipId(17);
        var bob = TalkIds.MembershipId(19);

        var start = await Feed().StartAsync(Target(SubscriptionResourceKind.ChatMembers, OneToOne), Ct);
        var batch = await Feed().ChangesAsync(Target(SubscriptionResourceKind.ChatMembers, OneToOne), State(new { ids = new[] { alice, "gone" } }), Ct);

        Read<TalkChangeFeed.MembersState>(start.Value).Ids.ShouldBe([alice, bob]);
        batch.Value.Changes.Select(c => (c.Id, c.ChangeType, c.Resource)).ShouldBe([
            (bob, SubscriptionChangeType.Created, $"chats('{OneToOne}')/members('{bob}')"),
            ("gone", SubscriptionChangeType.Deleted, $"chats('{OneToOne}')/members('gone')"),
        ]);
    }

    [Fact]
    public async Task A_state_that_isnt_the_resources_is_gone()
    {
        (await Feed().ChangesAsync(Target(SubscriptionResourceKind.ChatMessages, OneToOne), "not base64!", Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }
}
