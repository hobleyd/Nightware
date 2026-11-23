using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Tests.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Teams;

/// <summary>The Talk calls phase 24 added to the shared client: team rooms, room deletion and descriptions, thread renames, newer messages.</summary>
public sealed class TalkChannelClientTests : IDisposable
{
    private const string Token = TeamTestServer.General;
    private const string Chat = TalkTestServer.Chat + "/" + Token;
    private readonly TeamTestServer _teams = new();

    public void Dispose() => _teams.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_team_room_without_a_description_sends_none()
    {
        _teams.Reply("POST", TalkTestServer.Rooms, 201, """{"id":41,"token":"newroom1","type":2,"name":"Ideas"}""");

        (await _teams.Talk.Client.CreateTeamRoomAsync("Ideas", null, TeamTestServer.TeamId, Ct)).Value.Token.ShouldBe("newroom1");
        JsonNode.Parse(TeamTestServer.Body(_teams.Requests[^1]))!.AsObject().ContainsKey("description").ShouldBeFalse();
    }

    [Fact]
    public async Task A_refused_team_room_is_passed_on()
    {
        _teams.Reply("POST", TalkTestServer.Rooms, 403, """{"error":"permissions"}""");

        (await _teams.Talk.Client.CreateTeamRoomAsync("Ideas", "d", TeamTestServer.TeamId, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Rooms_are_deleted_and_described_threads_renamed()
    {
        _teams.Reply("DELETE", TalkTestServer.Rooms + "/" + Token, 200, "null");
        _teams.Reply("PUT", TalkTestServer.Rooms + "/" + Token + "/description", 200, "null");
        _teams.Reply("PUT", Chat + "/threads/209", 200, "{}");
        _teams.Reply("PUT", Chat + "/threads/999", 404, "{}");

        (await _teams.Talk.Client.DeleteRoomAsync(Token, Ct)).IsSuccess.ShouldBeTrue();
        (await _teams.Talk.Client.SetDescriptionAsync(Token, "About", Ct)).IsSuccess.ShouldBeTrue();
        TeamTestServer.Body(_teams.Requests[^1]).ShouldBe("description=About");
        (await _teams.Talk.Client.RenameThreadAsync(Token, 209, "New", Ct)).IsSuccess.ShouldBeTrue();
        TeamTestServer.Body(_teams.Requests[^1]).ShouldBe("threadTitle=New");
        (await _teams.Talk.Client.RenameThreadAsync(Token, 999, "New", Ct)).Error!.Message.ShouldContain("message");
        (await _teams.Talk.Client.DeleteRoomAsync("../x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Newer_messages_come_oldest_first_and_say_where_to_read_on()
    {
        _teams.ReplyFixture("GET", Chat, "channel-future.json", new Dictionary<string, string> { ["X-Chat-Last-Given"] = "219" });

        var page = await _teams.Talk.Client.FutureAsync(Token, 211, 100, Ct);

        page.Value.Messages.Select(m => m.Id).ShouldBe([212L, 213L, 214L, 219L]);
        page.Value.LastGiven.ShouldBe(219);
        var request = _teams.Requests[^1];
        TalkTestServer.Query(request, "lookIntoFuture").ShouldBe("1");
        TalkTestServer.Query(request, "timeout").ShouldBe("0");
        TalkTestServer.Query(request, "lastKnownMessageId").ShouldBe("211");
        TalkTestServer.Query(request, "setReadMarker").ShouldBe("0");
    }

    [Fact]
    public async Task Without_the_header_the_newest_given_is_where_to_read_on_and_nothing_newer_is_the_end()
    {
        _teams.ReplyFixtureWhen("GET", Chat, "lastKnownMessageId", "211", "channel-future.json");
        _teams.NotModifiedWhen(Chat, "lastKnownMessageId", "219");

        (await _teams.Talk.Client.FutureAsync(Token, 211, 100, Ct)).Value.LastGiven.ShouldBe(219);
        var end = await _teams.Talk.Client.FutureAsync(Token, 219, 100, Ct);
        end.Value.Messages.ShouldBeEmpty();
        end.Value.LastGiven.ShouldBeNull();
    }

    [Fact]
    public async Task Newer_messages_of_a_room_gone_are_not_found()
    {
        _teams.Reply("GET", Chat, 404, "null");

        (await _teams.Talk.Client.FutureAsync(Token, 1, 10, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Talk.Client.FutureAsync("../x", 1, 10, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_rooms_description_is_read()
    {
        _teams.ReplyFixture("GET", TalkTestServer.Rooms, "rooms.json");

        (await _teams.Talk.Client.ListRoomsAsync(Ct)).Value.Single(r => r.Token == TeamTestServer.Design).Description.ShouldBe("UI and UX work");
    }
}
