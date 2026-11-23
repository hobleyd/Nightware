using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Teams;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Teams;

/// <summary>A team's members on the recorded team: alice (its creator, level 9) and bob (level 1).</summary>
public sealed class NextcloudTeamMembersTests : IDisposable
{
    private const string Members = TeamTestServer.Circle + "/members";
    private const string AliceMembership = "m." + TeamTestServer.AliceMember;
    private const string BobMembership = "m." + TeamTestServer.BobMember;
    private readonly TeamTestServer _teams = new();

    public NextcloudTeamMembersTests()
    {
        _teams.ReplyFixture("GET", TeamTestServer.Circles, "circles.json");
        _teams.ReplyFixture("GET", Members, "members.json");
    }

    public void Dispose() => _teams.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Members_are_directory_people_and_level_8_or_9_is_an_owner()
    {
        var members = await _teams.Members().ListAsync(TeamTestServer.TeamId, Ct);

        members.Value.Count.ShouldBe(2);
        var alice = members.Value[0];
        alice.Id.ShouldBe(AliceMembership);
        alice.UserId.ShouldBe("id-alice");
        alice.Email.ShouldBe("alice@example.com");
        alice.DisplayName.ShouldBe("Alice Anderson");
        alice.Roles.ShouldBe([ChatMember.OwnerRole]);
        members.Value[1].Roles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Groups_and_other_kinds_of_member_are_left_out_and_the_unknown_keep_their_name()
    {
        using var mixed = new TeamTestServer();
        mixed.ReplyFixture("GET", TeamTestServer.Circles, "circles.json");
        mixed.Reply("GET", Members, 200,
            """[{"id":"GroupGroupGroupGroupGroupGroup1","userId":"staff","userType":2,"level":1},{"id":"GuestGuestGuestGuestGuestGuest1","userId":"carol","userType":1,"level":1,"displayName":"Carol"}]""");

        var member = (await mixed.Members().ListAsync(TeamTestServer.TeamId, Ct)).Value.ShouldHaveSingleItem();
        member.DisplayName.ShouldBe("Carol");
        member.UserId.ShouldBeNull();
    }

    [Fact]
    public async Task A_member_is_found_by_membership_id()
    {
        (await _teams.Members().GetAsync(TeamTestServer.TeamId, BobMembership, Ct)).Value.UserId.ShouldBe("id-bob");
        (await _teams.Members().GetAsync(TeamTestServer.TeamId, "m.zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Members().GetAsync(TeamTestServer.TeamId, TeamTestServer.BobMember, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Members().GetAsync("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", BobMembership, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Adding_an_owner_adds_then_raises_the_level()
    {
        _teams.Reply("POST", Members, 200, $$"""{"id":"{{TeamTestServer.BobMember}}","userId":"bob","userType":1,"level":1,"displayName":"Bob Brown"}""");
        _teams.Reply("PUT", $"{Members}/{TeamTestServer.BobMember}/level", 200, "{}");

        var added = await _teams.Members().AddAsync(TeamTestServer.TeamId, new ChatMemberDraft("id-bob", true), Ct);

        added.Value.Id.ShouldBe(BobMembership);
        added.Value.Roles.ShouldBe([ChatMember.OwnerRole]);
        added.Value.UserId.ShouldBe("id-bob");
        JsonNode.Parse(TeamTestServer.Body(_teams.Requests[^1]))!["level"]!.GetValue<int>().ShouldBe(CircleLevel.Admin);
    }

    [Fact]
    public async Task Adding_a_member_needs_a_known_person_and_the_team()
    {
        (await _teams.Members().AddAsync(TeamTestServer.TeamId, new ChatMemberDraft("id-nobody", false), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _teams.Members().AddAsync("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", new ChatMemberDraft("id-bob", false), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _teams.To("POST", Members).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_addition_is_passed_on()
    {
        _teams.Reply("POST", Members, 400, "[]");

        (await _teams.Members().AddAsync(TeamTestServer.TeamId, new ChatMemberDraft("id-bob", false), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Roles_set_level_8_or_1_and_an_unchanged_role_sends_nothing()
    {
        _teams.Reply("PUT", $"{Members}/{TeamTestServer.BobMember}/level", 200, "{}");

        (await _teams.Members().SetOwnerAsync(TeamTestServer.TeamId, BobMembership, false, Ct)).Value.Roles.ShouldBeEmpty();
        _teams.Requests.ShouldAllBe(r => r.RequestMessage!.Method == "GET");

        (await _teams.Members().SetOwnerAsync(TeamTestServer.TeamId, BobMembership, true, Ct)).Value.Roles.ShouldBe([ChatMember.OwnerRole]);
        JsonNode.Parse(TeamTestServer.Body(_teams.Requests[^1]))!["level"]!.GetValue<int>().ShouldBe(CircleLevel.Admin);
    }

    [Fact]
    public async Task The_creator_is_the_last_owner()
    {
        (await _teams.Members().SetOwnerAsync(TeamTestServer.TeamId, AliceMembership, true, Ct)).Value.Roles.ShouldBe([ChatMember.OwnerRole]);
        (await _teams.Members().SetOwnerAsync(TeamTestServer.TeamId, AliceMembership, false, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _teams.Members().RemoveAsync(TeamTestServer.TeamId, AliceMembership, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _teams.Requests.ShouldAllBe(r => r.RequestMessage!.Method == "GET");
    }

    [Fact]
    public async Task A_member_is_removed_and_the_caller_leaves()
    {
        _teams.Reply("DELETE", $"{Members}/{TeamTestServer.BobMember}", 200, "{}");
        (await _teams.Members().RemoveAsync(TeamTestServer.TeamId, BobMembership, Ct)).IsSuccess.ShouldBeTrue();

        // bob, a member, removing himself leaves.
        using var bob = new TeamTestServer();
        bob.ReplyFixture("GET", TeamTestServer.Circles, "circles-bob.json");
        bob.ReplyFixture("GET", Members, "members.json");
        bob.Reply("PUT", TeamTestServer.Circle + "/leave", 200, "{}");
        (await bob.Members().RemoveAsync(TeamTestServer.TeamId, BobMembership, Ct)).IsSuccess.ShouldBeTrue();
        bob.To("PUT", TeamTestServer.Circle + "/leave").ShouldHaveSingleItem();
        bob.To("DELETE", $"{Members}/{TeamTestServer.BobMember}").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_directory_failure_fails_the_listing()
    {
        _teams.Talk.Names.UserOfAccountAsync("bob", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        (await _teams.Members().ListAsync(TeamTestServer.TeamId, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Theory]
    [InlineData("m." + TeamTestServer.BobMember, true)]
    [InlineData(TeamTestServer.BobMember, false)]
    [InlineData("m.short", false)]
    [InlineData("m.../../../../../../../../../../../../", false)]
    [InlineData(null, false)]
    public void Membership_ids_are_m_dot_and_a_circle_member_id(string? membershipId, bool valid) =>
        NextcloudTeamMembers.TryMemberId(membershipId, out _).ShouldBe(valid);
}
