using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Tests.Chats;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;
using Nightware.Domain.Teams;

namespace Nightware.Adapters.Dav.Tests.Teams;

/// <summary>Teams over circles, and their members, on the recorded team.</summary>
public sealed class NextcloudTeamStoreTests : IDisposable
{
    private const string NewTeam = "NewTeamNewTeamNewTeamNewTeam123";
    private const string NewCircle = TeamTestServer.Circles + "/" + NewTeam;
    private readonly TeamTestServer _teams = new();

    public void Dispose() => _teams.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CreatedCircle(int config = 0) =>
        $$$"""{"id":"{{{NewTeam}}}","name":"New","config":{{{config}}},"creation":0,"initiator":{"id":"{{{TeamTestServer.AliceMember}}}","userId":"alice","userType":1,"level":9}}""";

    [Fact]
    public async Task Joined_teams_are_the_callers_circles_by_name()
    {
        _teams.ReplyFixture("GET", TeamTestServer.Circles, "circles.json");

        var teams = await _teams.Store().ListJoinedAsync(Ct);

        teams.Value.Select(t => t.DisplayName).ShouldBe(["P24 Probe Renamed", "Probe Team"]);
        var team = teams.Value[0];
        team.Id.ShouldBe(TeamTestServer.TeamId);
        team.Description.ShouldBe("About things");
        team.Visibility.ShouldBe(TeamVisibility.Public);
        team.CreatedDateTime.ShouldNotBeNull();
        team.WebUrl.ShouldNotBeNull();
        teams.Value[1].Description.ShouldBeNull();
        teams.Value[1].Visibility.ShouldBe(TeamVisibility.Private);
    }

    [Fact]
    public async Task A_team_is_one_of_the_callers()
    {
        _teams.ReplyFixture("GET", TeamTestServer.Circles, "circles.json");

        (await _teams.Store().GetAsync(TeamTestServer.TeamId, Ct)).Value.DisplayName.ShouldBe("P24 Probe Renamed");
        (await _teams.Store().GetAsync("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Creating_makes_the_circle_its_members_and_General()
    {
        _teams.Reply("POST", TeamTestServer.Circles, 200, CreatedCircle());
        _teams.Reply("PUT", NewCircle + "/description", 200, "{}");
        _teams.Reply("PUT", NewCircle + "/config", 200, "{}");
        _teams.Reply("POST", NewCircle + "/members", 200, $$"""{"id":"{{TeamTestServer.BobMember}}","userId":"bob","userType":1,"level":1}""");
        _teams.Reply("PUT", $"{NewCircle}/members/{TeamTestServer.BobMember}/level", 200, "{}");
        _teams.Reply("POST", TalkTestServer.Rooms, 201, """{"id":50,"token":"newgen01","type":2,"name":"General"}""");
        _teams.Reply("GET", TeamTestServer.Circles, 200, $"[{CreatedCircle(24).Replace("\"creation\":0", "\"creation\":1795429771", StringComparison.Ordinal)}]");

        var created = await _teams.Store().CreateAsync(
            new TeamCreation("New", "About", TeamVisibility.Public, [new ChatMemberDraft("id-bob", true), new ChatMemberDraft("id-alice", true)]), Ct);

        created.Value.Id.ShouldBe(NewTeam);
        created.Value.CreatedDateTime.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1795429771));
        JsonNode.Parse(TeamTestServer.Body(_teams.To("PUT", NewCircle + "/config").Single()))!["value"]!.GetValue<int>().ShouldBe(24);
        JsonNode.Parse(TeamTestServer.Body(_teams.To("PUT", NewCircle + "/description").Single()))!["value"]!.GetValue<string>().ShouldBe("About");

        // The caller is the circle's owner already; bob is added and made an owner.
        var added = _teams.To("POST", NewCircle + "/members").ShouldHaveSingleItem();
        JsonNode.Parse(TeamTestServer.Body(added))!["userId"]!.GetValue<string>().ShouldBe("bob");
        JsonNode.Parse(TeamTestServer.Body(_teams.To("PUT", $"{NewCircle}/members/{TeamTestServer.BobMember}/level").Single()))!["level"]!.GetValue<int>().ShouldBe(8);

        var room = JsonNode.Parse(TeamTestServer.Body(_teams.To("POST", TalkTestServer.Rooms).Single()))!;
        room["roomType"]!.GetValue<int>().ShouldBe(2);
        room["roomName"]!.GetValue<string>().ShouldBe("General");
        room["participants"]!["teams"]![0]!.GetValue<string>().ShouldBe(NewTeam);
    }

    [Fact]
    public async Task A_plain_team_needs_no_description_visibility_or_members()
    {
        _teams.Reply("POST", TeamTestServer.Circles, 200, CreatedCircle());
        _teams.Reply("POST", TalkTestServer.Rooms, 201, """{"id":50,"token":"newgen01","type":2,"name":"General"}""");
        _teams.Reply("GET", TeamTestServer.Circles, 503);

        var created = await _teams.Store().CreateAsync(new TeamCreation("New", null, TeamVisibility.Private, []), Ct);

        // The re-read failed: the creation answer stands in for it.
        created.Value.Id.ShouldBe(NewTeam);
        created.Value.CreatedDateTime.ShouldBeNull();
        _teams.Requests.Count(r => r.RequestMessage!.Method == "PUT").ShouldBe(0);
    }

    [Fact]
    public async Task A_member_nobody_knows_stops_creation_before_anything_is_made()
    {
        var created = await _teams.Store().CreateAsync(new TeamCreation("New", null, null, [new ChatMemberDraft("id-nobody", false)]), Ct);

        created.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _teams.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_team_that_cant_be_finished_is_taken_down_again()
    {
        _teams.Reply("POST", TeamTestServer.Circles, 200, CreatedCircle());
        _teams.Reply("POST", TalkTestServer.Rooms, 400, """{"error":"name"}""");
        _teams.Reply("GET", TalkTestServer.Ocs + "teams/" + NewTeam + "/resources", 200, """{"resources":[]}""");
        _teams.Reply("DELETE", NewCircle, 200, "{}");

        var created = await _teams.Store().CreateAsync(new TeamCreation("New", null, null, []), Ct);

        created.Error!.Kind.ShouldBe(ErrorKind.Validation);
        _teams.To("DELETE", NewCircle).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_failed_circle_creation_is_passed_on()
    {
        _teams.Reply("POST", TeamTestServer.Circles, 400, "[]");

        (await _teams.Store().CreateAsync(new TeamCreation("New", null, null, []), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Updating_changes_only_what_differs()
    {
        _teams.ReplyFixture("GET", TeamTestServer.Circles, "circles.json");
        _teams.Reply("PUT", TeamTestServer.Circle + "/name", 200, "{}");
        _teams.Reply("PUT", TeamTestServer.Circle + "/config", 200, "{}");

        var updated = await _teams.Store().UpdateAsync(TeamTestServer.TeamId, new TeamUpdate("Renamed", "About things", TeamVisibility.Private), Ct);

        updated.IsSuccess.ShouldBeTrue();
        JsonNode.Parse(TeamTestServer.Body(_teams.To("PUT", TeamTestServer.Circle + "/name").Single()))!["value"]!.GetValue<string>().ShouldBe("Renamed");
        JsonNode.Parse(TeamTestServer.Body(_teams.To("PUT", TeamTestServer.Circle + "/config").Single()))!["value"]!.GetValue<int>().ShouldBe(0);
        _teams.To("PUT", TeamTestServer.Circle + "/description").ShouldBeEmpty();

        (await _teams.Store().UpdateAsync(TeamTestServer.TeamId, new TeamUpdate("P24 Probe Renamed", null, TeamVisibility.Public), Ct)).IsSuccess.ShouldBeTrue();
        _teams.Requests.Count(r => r.RequestMessage!.Method == "PUT").ShouldBe(2);
    }

    [Fact]
    public async Task Updating_stops_at_the_first_refusal()
    {
        _teams.ReplyFixture("GET", TeamTestServer.Circles, "circles.json");
        _teams.Reply("PUT", TeamTestServer.Circle + "/description", 403, "{}");

        (await _teams.Store().UpdateAsync(TeamTestServer.TeamId, new TeamUpdate(null, "New", TeamVisibility.Private), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _teams.To("PUT", TeamTestServer.Circle + "/config").ShouldBeEmpty();
        (await _teams.Store().UpdateAsync("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", new TeamUpdate("x", null, null), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Deleting_deletes_every_room_then_the_circle()
    {
        _teams.RecordedTeam();
        _teams.Reply("DELETE", TalkTestServer.Rooms + "/" + TeamTestServer.General, 200, "null");
        _teams.Reply("DELETE", TalkTestServer.Rooms + "/" + TeamTestServer.Design, 404, "null");
        _teams.Reply("DELETE", TeamTestServer.Circle, 200, "{}");

        (await _teams.Store().DeleteAsync(TeamTestServer.TeamId, Ct)).IsSuccess.ShouldBeTrue();

        var deletes = _teams.Requests.Where(r => r.RequestMessage!.Method == "DELETE").Select(r => r.RequestMessage!.AbsolutePath).ToList();
        deletes[^1].ShouldBe(TeamTestServer.Circle);
        deletes.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_room_that_cant_be_deleted_keeps_the_circle()
    {
        _teams.RecordedTeam();
        _teams.Reply("DELETE", TalkTestServer.Rooms + "/" + TeamTestServer.Design, 200, "null");
        _teams.Reply("DELETE", TalkTestServer.Rooms + "/" + TeamTestServer.General, 503);

        (await _teams.Store().DeleteAsync(TeamTestServer.TeamId, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _teams.To("DELETE", TeamTestServer.Circle).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_owner_deletes_and_only_when_they_moderate_every_channel()
    {
        _teams.ReplyFixture("GET", TeamTestServer.Circles, "circles-bob.json");
        (await _teams.Store().DeleteAsync(TeamTestServer.TeamId, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        using var owner = new TeamTestServer();
        owner.ReplyFixture("GET", TeamTestServer.Circles, "circles.json");
        owner.Reply("GET", TeamTestServer.Resources, 200, """{"resources":[{"id":"bobroom1","provider":{"id":"talk"}}]}""");
        owner.Reply("GET", TalkTestServer.Rooms, 200, """[{"id":60,"token":"bobroom1","type":2,"name":"Bob room","participantType":3}]""");

        (await owner.Store().DeleteAsync(TeamTestServer.TeamId, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        owner.Requests.ShouldAllBe(r => r.RequestMessage!.Method == "GET");
    }

    [Fact]
    public async Task A_team_without_channels_is_just_destroyed()
    {
        _teams.ReplyFixture("GET", TeamTestServer.Circles, "circles.json");
        _teams.Reply("GET", TeamTestServer.Resources, 200, """{"resources":[]}""");
        _teams.Reply("DELETE", TeamTestServer.Circle, 200, "{}");

        (await _teams.Store().DeleteAsync(TeamTestServer.TeamId, Ct)).IsSuccess.ShouldBeTrue();
        _teams.To("GET", TalkTestServer.Rooms).ShouldBeEmpty();
    }
}
