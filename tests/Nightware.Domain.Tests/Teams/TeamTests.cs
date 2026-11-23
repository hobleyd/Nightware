using Nightware.Domain.Teams;

namespace Nightware.Domain.Tests.Teams;

public sealed class TeamTests
{
    private const string TeamId = "6LOrxifQ5p4vRt2I3iplImWqdpjlADT";

    [Theory]
    [InlineData(TeamId, true)]
    [InlineData("6LOrxifQ5p4vRt2I3iplImWqdpjlAD", false)]
    [InlineData("6LOrxifQ5p4vRt2I3iplImWqdpjlADTx", false)]
    [InlineData("6LOrxifQ5p4vRt2I3iplImWqdp-lADT", false)]
    [InlineData("5d3f2a1b-9c4e-4b7a-8f0d-2e6c1a9b7d44", false)]
    [InlineData(null, false)]
    public void A_team_id_is_31_alphanumerics_and_never_a_directory_group_uuid(string? id, bool isTeam) =>
        Team.IsTeamId(id).ShouldBe(isTeam);

    [Fact]
    public void A_creation_operation_id_names_its_team()
    {
        var id = TeamCreationOperation.IdFor(TeamId);

        TeamCreationOperation.TeamOf(id).ShouldBe(TeamId);
        TeamCreationOperation.TeamOf(TeamId).ShouldBeNull();
        TeamCreationOperation.TeamOf("create.not-a-team").ShouldBeNull();
        TeamCreationOperation.TeamOf(null).ShouldBeNull();
    }

    [Fact]
    public void A_team_is_private_by_default_and_a_channel_standard()
    {
        new Team(TeamId, "Team").Visibility.ShouldBe(TeamVisibility.Private);
        Channel.StandardMembershipType.ShouldBe("standard");
        new Channel("abcd1234", "General").IsPrimary.ShouldBeFalse();
    }

    [Fact]
    public void An_update_without_changes_is_empty()
    {
        new TeamUpdate(null, null, null).IsEmpty.ShouldBeTrue();
        new TeamUpdate(null, "d", null).IsEmpty.ShouldBeFalse();
        new ChannelUpdate(null, null).IsEmpty.ShouldBeTrue();
        new ChannelUpdate("n", null).IsEmpty.ShouldBeFalse();
    }
}
