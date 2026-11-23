using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Teams;
using Nightware.Adapters.Dav.Tests.Chats;
using Nightware.Domain.Kernel;
using Nightware.Domain.Teams;

namespace Nightware.Adapters.Dav.Tests.Teams;

/// <summary>Channels of the recorded team: General (room 39, the primary one) and Design (room 40, described).</summary>
public sealed class TalkChannelStoreTests : IDisposable
{
    private readonly TeamTestServer _teams = new();

    public TalkChannelStoreTests() => _teams.RecordedTeam();

    public void Dispose() => _teams.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_primary_channel_comes_first_then_the_rest_by_name()
    {
        var channels = await _teams.Channels.ListAsync(TeamTestServer.TeamId, Ct);

        channels.Value.Select(c => (c.Id, c.DisplayName, c.IsPrimary)).ShouldBe([(TeamTestServer.General, "General", true), (TeamTestServer.Design, "Design", false)]);
        channels.Value[1].Description.ShouldBe("UI and UX work");
        channels.Value[0].Description.ShouldBeNull();
        channels.Value[0].WebUrl.ShouldBe(TalkTestServer.PublicBase + "call/" + TeamTestServer.General);
        channels.Value[0].CreatedDateTime.ShouldBeNull();
    }

    [Fact]
    public async Task A_channel_and_the_primary_one_are_found_by_token()
    {
        (await _teams.Channels.GetAsync(TeamTestServer.TeamId, TeamTestServer.Design, Ct)).Value.IsPrimary.ShouldBeFalse();
        (await _teams.Channels.GetPrimaryAsync(TeamTestServer.TeamId, Ct)).Value.Id.ShouldBe(TeamTestServer.General);

        // A room of the caller's that isn't the team's is no channel of it.
        (await _teams.Channels.GetAsync(TeamTestServer.TeamId, "indwxaa7", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Channels.GetAsync("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", TeamTestServer.General, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_team_without_rooms_has_no_channels_and_no_primary_one()
    {
        using var empty = new TeamTestServer();
        empty.ReplyFixture("GET", TeamTestServer.Circles, "circles.json");
        empty.Reply("GET", TeamTestServer.Resources, 200, """{"resources":[]}""");

        (await empty.Channels.ListAsync(TeamTestServer.TeamId, Ct)).Value.ShouldBeEmpty();
        (await empty.Channels.GetPrimaryAsync(TeamTestServer.TeamId, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        empty.To("GET", TalkTestServer.Rooms).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_room_list_fails_the_channels()
    {
        using var failing = new TeamTestServer();
        failing.ReplyFixture("GET", TeamTestServer.Circles, "circles.json");
        failing.ReplyFixture("GET", TeamTestServer.Resources, "resources.json");
        failing.Reply("GET", TalkTestServer.Rooms, 503);

        (await failing.Channels.ListAsync(TeamTestServer.TeamId, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Creating_makes_a_room_with_the_team_in_it()
    {
        _teams.Reply("POST", TalkTestServer.Rooms, 201, """{"id":41,"token":"newroom1","type":2,"name":"Ideas","displayName":"Ideas","description":"Thoughts"}""");

        var created = await _teams.Channels.CreateAsync(TeamTestServer.TeamId, new ChannelCreation("Ideas", "Thoughts"), Ct);

        created.Value.ShouldBe(new Channel("newroom1", "Ideas") { Description = "Thoughts", WebUrl = TalkTestServer.PublicBase + "call/newroom1" });
        var body = JsonNode.Parse(TeamTestServer.Body(_teams.To("POST", TalkTestServer.Rooms).Single()))!;
        body["roomType"]!.GetValue<int>().ShouldBe(2);
        body["roomName"]!.GetValue<string>().ShouldBe("Ideas");
        body["description"]!.GetValue<string>().ShouldBe("Thoughts");
        body["participants"]!["teams"]![0]!.GetValue<string>().ShouldBe(TeamTestServer.TeamId);
    }

    [Fact]
    public async Task A_name_already_taken_is_a_conflict()
    {
        var created = await _teams.Channels.CreateAsync(TeamTestServer.TeamId, new ChannelCreation("design", null), Ct);

        created.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        created.Error.Code.ShouldBe("NameAlreadyExists");
        _teams.To("POST", TalkTestServer.Rooms).ShouldBeEmpty();
    }

    [Fact]
    public async Task Updating_renames_and_redescribes_the_room()
    {
        _teams.Reply("PUT", TalkTestServer.Rooms + "/" + TeamTestServer.Design, 200, "null");
        _teams.Reply("PUT", TalkTestServer.Rooms + "/" + TeamTestServer.Design + "/description", 200, "null");

        (await _teams.Channels.UpdateAsync(TeamTestServer.TeamId, TeamTestServer.Design, new ChannelUpdate("Design team", "New"), Ct)).IsSuccess.ShouldBeTrue();

        TeamTestServer.Body(_teams.To("PUT", TalkTestServer.Rooms + "/" + TeamTestServer.Design).Single()).ShouldContain("roomName=Design+team");
        TeamTestServer.Body(_teams.To("PUT", TalkTestServer.Rooms + "/" + TeamTestServer.Design + "/description").Single()).ShouldContain("description=New");
    }

    [Fact]
    public async Task An_unchanged_name_and_description_send_nothing()
    {
        (await _teams.Channels.UpdateAsync(TeamTestServer.TeamId, TeamTestServer.Design, new ChannelUpdate("Design", "UI and UX work"), Ct)).IsSuccess.ShouldBeTrue();
        (await _teams.Channels.UpdateAsync(TeamTestServer.TeamId, TeamTestServer.General, new ChannelUpdate("General", null), Ct)).IsSuccess.ShouldBeTrue();
        _teams.Requests.ShouldAllBe(r => r.RequestMessage!.Method == "GET");
    }

    [Fact]
    public async Task General_cant_be_renamed_and_no_two_channels_share_a_name()
    {
        (await _teams.Channels.UpdateAsync(TeamTestServer.TeamId, TeamTestServer.General, new ChannelUpdate("Lobby", null), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _teams.Channels.UpdateAsync(TeamTestServer.TeamId, TeamTestServer.Design, new ChannelUpdate("GENERAL", null), Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
        (await _teams.Channels.UpdateAsync(TeamTestServer.TeamId, "nochanne", new ChannelUpdate("x", null), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _teams.Requests.ShouldAllBe(r => r.RequestMessage!.Method == "GET");
    }

    [Fact]
    public async Task A_refused_rename_is_passed_on()
    {
        _teams.Reply("PUT", TalkTestServer.Rooms + "/" + TeamTestServer.Design, 403, "null");

        (await _teams.Channels.UpdateAsync(TeamTestServer.TeamId, TeamTestServer.Design, new ChannelUpdate("Other", "x"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _teams.To("PUT", TalkTestServer.Rooms + "/" + TeamTestServer.Design + "/description").ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_deletes_the_room_but_never_General()
    {
        _teams.Reply("DELETE", TalkTestServer.Rooms + "/" + TeamTestServer.Design, 200, "null");

        (await _teams.Channels.DeleteAsync(TeamTestServer.TeamId, TeamTestServer.Design, Ct)).IsSuccess.ShouldBeTrue();
        (await _teams.Channels.DeleteAsync(TeamTestServer.TeamId, TeamTestServer.General, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _teams.To("DELETE", TalkTestServer.Rooms + "/" + TeamTestServer.General).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_files_folder_is_the_channels_name_in_the_team_folder()
    {
        _teams.ReplyFixture("GET", TeamTestServer.Folder, "folder.json");

        (await _teams.Channels.FilesFolderPathAsync(TeamTestServer.TeamId, TeamTestServer.Design, Ct)).Value.ShouldBe(["P24 Probe 1", "Design"]);
        (await _teams.Channels.FilesFolderPathAsync(TeamTestServer.TeamId, "nochanne", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_team_without_a_folder_has_no_files_folder()
    {
        _teams.Reply("GET", TeamTestServer.Folder, 404, "[]");

        (await _teams.Channels.FilesFolderPathAsync(TeamTestServer.TeamId, TeamTestServer.Design, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Theory]
    [InlineData("Design", "Design")]
    [InlineData(" a/b\\c ", "a_b_c")]
    [InlineData("..", "_")]
    [InlineData("tab\there", "tab_here")]
    public void A_folder_name_holds_no_separators(string name, string folder) =>
        TalkChannelStore.FolderName(name).ShouldBe(folder);

    [Fact]
    public async Task A_conversation_is_one_of_the_teams_rooms()
    {
        _teams.Reply("GET", TalkTestServer.Rooms + "/" + TeamTestServer.General, 200, TeamTestServer.RoomData(TeamTestServer.General));

        var conversation = await _teams.Channels.ConversationAsync(TeamTestServer.TeamId, TeamTestServer.General, Ct);

        conversation.Value.Channel!.ShouldBe(new Nightware.Domain.Chats.ChannelIdentity(TeamTestServer.TeamId, TeamTestServer.General));
        conversation.Value.DisplayName.ShouldBe("General");
        (await _teams.Channels.ConversationAsync(TeamTestServer.TeamId, "indwxaa7", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Channels.ConversationAsync(TeamTestServer.TeamId, "../x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_room_gone_since_the_resources_were_read_is_no_channel()
    {
        _teams.Reply("GET", TalkTestServer.Rooms + "/" + TeamTestServer.Design, 404, "null");

        (await _teams.Channels.ConversationAsync(TeamTestServer.TeamId, TeamTestServer.Design, Ct)).Error!.Message.ShouldBe(TalkChannelStore.NoChannel().Message);
    }
}
