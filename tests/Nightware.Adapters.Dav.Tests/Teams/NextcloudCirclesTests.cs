using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Teams;
using Nightware.Adapters.Dav.Tests.Chats;
using Nightware.Domain.Kernel;
using Nightware.Domain.Teams;

namespace Nightware.Adapters.Dav.Tests.Teams;

/// <summary>The circles app's OCS API as the teams adapter calls it.</summary>
public sealed class NextcloudCirclesTests : IDisposable
{
    private readonly TeamTestServer _teams = new();

    public void Dispose() => _teams.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_callers_teams_with_their_own_membership()
    {
        _teams.ReplyFixture("GET", TeamTestServer.Circles, "circles.json");

        var circles = await _teams.CirclesClient.ListAsync(Ct);

        circles.Value.Select(c => c.Id).ShouldBe([TeamTestServer.TeamId, TeamTestServer.OtherTeamId]);
        var team = circles.Value[0];
        team.Name.ShouldBe("P24 Probe Renamed");
        team.Description.ShouldBe("About things");
        team.Config.ShouldBe(CircleConfig.Public);
        team.Creation.ShouldNotBeNull();
        team.Url.ShouldBe($"https://cloud.example.com/cloud/apps/circles/teams/team/{TeamTestServer.TeamId}");
        team.Caller!.Id.ShouldBe(TeamTestServer.AliceMember);
        team.Caller.Level.ShouldBe(CircleLevel.Owner);
    }

    [Fact]
    public async Task Single_personal_system_and_hidden_circles_are_not_teams()
    {
        static string Circle(string id, int config) => $$$"""{"id":"{{{id}}}","name":"c","config":{{{config}}},"initiator":{"id":"{{{TeamTestServer.AliceMember}}}","userId":"alice","userType":1,"level":1}}""";
        _teams.Reply("GET", TeamTestServer.Circles, 200,
            $"[{Circle("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1", 1)},{Circle("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2", 2)},{Circle("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa3", 4)},{Circle("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa4", 1024)},{Circle("short", 0)},{Circle("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa5", 8)}]");

        (await _teams.CirclesClient.ListAsync(Ct)).Value.Select(c => c.Id).ShouldBe(["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa5"]);
    }

    [Fact]
    public async Task A_team_is_found_in_the_callers_list_never_by_asking_for_it()
    {
        _teams.ReplyFixture("GET", TeamTestServer.Circles, "circles.json");

        (await _teams.CirclesClient.GetAsync(TeamTestServer.TeamId, Ct)).Value.Id.ShouldBe(TeamTestServer.TeamId);
        (await _teams.CirclesClient.GetAsync("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.CirclesClient.GetAsync("../x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _teams.Requests.ShouldAllBe(r => r.RequestMessage!.AbsolutePath == TeamTestServer.Circles);
    }

    [Fact]
    public async Task A_list_that_is_not_one_is_unexpected_and_a_failure_is_passed_on()
    {
        _teams.Reply("GET", TeamTestServer.Circles, 200, "{}");
        (await _teams.CirclesClient.ListAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);

        using var failing = new TeamTestServer();
        failing.Reply("GET", TeamTestServer.Circles, 503);
        (await failing.CirclesClient.ListAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Creating_sends_json_with_a_team_folder()
    {
        _teams.Reply("POST", TeamTestServer.Circles, 200, $$$"""{"id":"{{{TeamTestServer.TeamId}}}","name":"New","config":0,"creation":0,"initiator":{"id":"{{{TeamTestServer.AliceMember}}}","userId":"alice","userType":1,"level":9}}""");

        var created = await _teams.CirclesClient.CreateAsync("New", Ct);

        created.Value.Id.ShouldBe(TeamTestServer.TeamId);
        created.Value.Creation.ShouldBeNull();
        var body = JsonNode.Parse(TeamTestServer.Body(_teams.Requests[^1]))!;
        body["name"]!.GetValue<string>().ShouldBe("New");
        body["createTeamFolder"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task A_creation_answer_without_an_id_is_unexpected()
    {
        _teams.Reply("POST", TeamTestServer.Circles, 200, """{"name":"New"}""");

        (await _teams.CirclesClient.CreateAsync("New", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Edits_send_their_value_as_json()
    {
        _teams.Reply("PUT", TeamTestServer.Circle + "/name", 200, "{}");
        _teams.Reply("PUT", TeamTestServer.Circle + "/description", 200, "{}");
        _teams.Reply("PUT", TeamTestServer.Circle + "/config", 200, "{}");
        _teams.Reply("PUT", TeamTestServer.Circle + "/leave", 200, "{}");
        _teams.Reply("DELETE", TeamTestServer.Circle, 200, "{}");

        (await _teams.CirclesClient.RenameAsync(TeamTestServer.TeamId, "Renamed", Ct)).IsSuccess.ShouldBeTrue();
        JsonNode.Parse(TeamTestServer.Body(_teams.Requests[^1]))!["value"]!.GetValue<string>().ShouldBe("Renamed");
        (await _teams.CirclesClient.DescribeAsync(TeamTestServer.TeamId, "About", Ct)).IsSuccess.ShouldBeTrue();
        JsonNode.Parse(TeamTestServer.Body(_teams.Requests[^1]))!["value"]!.GetValue<string>().ShouldBe("About");
        (await _teams.CirclesClient.ConfigureAsync(TeamTestServer.TeamId, 24, Ct)).IsSuccess.ShouldBeTrue();
        JsonNode.Parse(TeamTestServer.Body(_teams.Requests[^1]))!["value"]!.GetValue<int>().ShouldBe(24);
        (await _teams.CirclesClient.LeaveAsync(TeamTestServer.TeamId, Ct)).IsSuccess.ShouldBeTrue();
        (await _teams.CirclesClient.DestroyAsync(TeamTestServer.TeamId, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_refused_edit_is_forbidden_and_a_missing_team_not_found()
    {
        _teams.Reply("PUT", TeamTestServer.Circle + "/name", 403, """{"message":"Insufficient permissions to perform this action"}""");
        _teams.Reply("PUT", TeamTestServer.Circle + "/description", 404, "[]");

        (await _teams.CirclesClient.RenameAsync(TeamTestServer.TeamId, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await _teams.CirclesClient.DescribeAsync(TeamTestServer.TeamId, "x", Ct)).Error!.Message.ShouldBe(NextcloudCircles.NoTeam().Message);
    }

    [Fact]
    public async Task Members_are_read_and_added_as_users()
    {
        _teams.ReplyFixture("GET", TeamTestServer.Circle + "/members", "members.json");
        _teams.Reply("POST", TeamTestServer.Circle + "/members", 200, $$"""{"id":"{{TeamTestServer.BobMember}}","userId":"bob","userType":1,"level":1,"displayName":"Bob Brown"}""");

        var members = await _teams.CirclesClient.MembersAsync(TeamTestServer.TeamId, Ct);
        members.Value.Select(m => (m.Id, m.UserId, m.Level)).ShouldBe([(TeamTestServer.AliceMember, "alice", 9), (TeamTestServer.BobMember, "bob", 1)]);
        members.Value.ShouldAllBe(m => m.IsUser);

        var added = await _teams.CirclesClient.AddUserAsync(TeamTestServer.TeamId, "bob", Ct);
        added.Value.Id.ShouldBe(TeamTestServer.BobMember);
        var body = JsonNode.Parse(TeamTestServer.Body(_teams.Requests[^1]))!;
        body["userId"]!.GetValue<string>().ShouldBe("bob");
        body["type"]!.GetValue<int>().ShouldBe(CircleMember.UserTypeUser);
    }

    [Fact]
    public async Task Malformed_ids_never_reach_the_server()
    {
        (await _teams.CirclesClient.MembersAsync("bad", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.CirclesClient.AddUserAsync("bad", "bob", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.CirclesClient.SetLevelAsync(TeamTestServer.TeamId, "../x", 8, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.CirclesClient.RemoveMemberAsync(TeamTestServer.TeamId, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.CirclesClient.FolderMountPointAsync("bad", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.CirclesClient.RoomTokensAsync("bad", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _teams.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_level_is_set_and_a_member_removed_by_member_id()
    {
        _teams.Reply("PUT", $"{TeamTestServer.Circle}/members/{TeamTestServer.BobMember}/level", 200, "{}");
        _teams.Reply("DELETE", $"{TeamTestServer.Circle}/members/{TeamTestServer.BobMember}", 200, "{}");

        (await _teams.CirclesClient.SetLevelAsync(TeamTestServer.TeamId, TeamTestServer.BobMember, CircleLevel.Admin, Ct)).IsSuccess.ShouldBeTrue();
        JsonNode.Parse(TeamTestServer.Body(_teams.Requests[^1]))!["level"]!.GetValue<int>().ShouldBe(8);
        (await _teams.CirclesClient.RemoveMemberAsync(TeamTestServer.TeamId, TeamTestServer.BobMember, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_member_answer_that_is_not_one_is_unexpected()
    {
        _teams.Reply("GET", TeamTestServer.Circle + "/members", 200, "{}");
        _teams.Reply("POST", TeamTestServer.Circle + "/members", 200, "[]");

        (await _teams.CirclesClient.MembersAsync(TeamTestServer.TeamId, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        (await _teams.CirclesClient.AddUserAsync(TeamTestServer.TeamId, "bob", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task The_team_folder_keeps_its_first_name_and_a_team_may_have_none()
    {
        _teams.ReplyFixture("GET", TeamTestServer.Folder, "folder.json");
        (await _teams.CirclesClient.FolderMountPointAsync(TeamTestServer.TeamId, Ct)).Value.ShouldBe("P24 Probe 1");

        using var none = new TeamTestServer();
        none.Reply("GET", TeamTestServer.Folder, 404, "[]");
        (await none.CirclesClient.FolderMountPointAsync(TeamTestServer.TeamId, Ct)).Value.ShouldBeNull();

        using var failing = new TeamTestServer();
        failing.Reply("GET", TeamTestServer.Folder, 503);
        (await failing.CirclesClient.FolderMountPointAsync(TeamTestServer.TeamId, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task The_teams_rooms_are_its_talk_resources()
    {
        _teams.ReplyFixture("GET", TeamTestServer.Resources, "resources.json");

        (await _teams.CirclesClient.RoomTokensAsync(TeamTestServer.TeamId, Ct)).Value.ShouldBe([TeamTestServer.General, TeamTestServer.Design]);
    }

    [Fact]
    public async Task Resources_of_a_team_gone_are_not_found()
    {
        _teams.Reply("GET", TeamTestServer.Resources, 404, "[]");

        (await _teams.CirclesClient.RoomTokensAsync(TeamTestServer.TeamId, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Theory]
    [InlineData(0, TeamVisibility.Private)]
    [InlineData(8, TeamVisibility.Private)]
    [InlineData(16, TeamVisibility.Private)]
    [InlineData(24, TeamVisibility.Public)]
    [InlineData(24 | 64, TeamVisibility.Public)]
    public void Visibility_is_public_when_visible_and_open(int config, TeamVisibility visibility) =>
        CircleConfig.VisibilityOf(config).ShouldBe(visibility);

    [Fact]
    public void Setting_visibility_keeps_the_other_flags()
    {
        CircleConfig.With(64, TeamVisibility.Public).ShouldBe(64 | 24);
        CircleConfig.With(64 | 24, TeamVisibility.Private).ShouldBe(64);
        CircleLevel.IsOwnerRole(CircleLevel.Admin).ShouldBeTrue();
        CircleLevel.IsOwnerRole(CircleLevel.Moderator).ShouldBeFalse();
    }

    [Fact]
    public void Every_recorded_path_is_below_the_ocs_root() =>
        TeamTestServer.Circles.ShouldStartWith(TalkTestServer.Ocs);
}
