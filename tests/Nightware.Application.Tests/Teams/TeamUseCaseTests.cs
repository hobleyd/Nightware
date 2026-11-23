using Nightware.Application.Files.Drives;
using Nightware.Application.Teams;
using Nightware.Domain.Chats;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;
using Nightware.Domain.Teams;

namespace Nightware.Application.Tests.Teams;

public sealed class TeamUseCaseTests
{
    private const string TeamId = "6LOrxifQ5p4vRt2I3iplImWqdpjlADT";
    private const string ChannelId = "xtugjbpy";
    private static readonly DateTimeOffset Created = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly Team Team = new(TeamId, "Probe") { CreatedDateTime = Created };
    private static readonly Channel General = new(ChannelId, "General") { IsPrimary = true };
    private static readonly ChatMember Bob = new("m.zjioHBheH4iNehqhIX2yQrHrn6RVDCd", "Bob Brown");

    private readonly ITeamStore _teams = Substitute.For<ITeamStore>();
    private readonly ITeamMembers _members = Substitute.For<ITeamMembers>();
    private readonly IChannelStore _channels = Substitute.For<IChannelStore>();
    private readonly IDriveStore _drive = Substitute.For<IDriveStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Design", true)]
    [InlineData("  Design  ", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("What?", false)]
    [InlineData("_hidden", false)]
    [InlineData(".hidden", false)]
    [InlineData("ends.", false)]
    [InlineData("q\"uote", false)]
    public void A_channel_name_follows_graphs_rules(string name, bool valid) =>
        (TeamRules.ChannelName(name, required: true) is null).ShouldBe(valid);

    [Fact]
    public void Channel_and_team_names_have_limits_and_may_be_left_out_of_a_change()
    {
        TeamRules.ChannelName(new string('x', Channel.MaxNameLength), true).ShouldBeNull();
        TeamRules.ChannelName(new string('x', Channel.MaxNameLength + 1), true)!.Target.ShouldBe("displayName");
        TeamRules.ChannelName(null, required: false).ShouldBeNull();
        TeamRules.ChannelName(null, required: true)!.Target.ShouldBe("displayName");
        TeamRules.TeamName(null, required: true)!.Target.ShouldBe("displayName");
        TeamRules.TeamName(" ", required: false)!.Target.ShouldBe("displayName");
        TeamRules.TeamName(new string('x', TeamRules.MaxTeamNameLength + 1), false).ShouldNotBeNull();
        TeamRules.Description(new string('x', TeamRules.MaxDescriptionLength + 1))!.Target.ShouldBe("description");
        TeamRules.Description(null).ShouldBeNull();
    }

    [Fact]
    public async Task Reads_pass_through()
    {
        _teams.ListJoinedAsync(Ct).Returns(Result<IReadOnlyList<Team>>.Success([Team]));
        _teams.GetAsync(TeamId, Ct).Returns(Team);
        _members.ListAsync(TeamId, Ct).Returns(Result<IReadOnlyList<ChatMember>>.Success([Bob]));
        _members.GetAsync(TeamId, Bob.Id, Ct).Returns(Bob);
        _channels.ListAsync(TeamId, Ct).Returns(Result<IReadOnlyList<Channel>>.Success([General]));
        _channels.GetAsync(TeamId, ChannelId, Ct).Returns(General);
        _channels.GetPrimaryAsync(TeamId, Ct).Returns(General);

        (await new ListJoinedTeams(_teams).ExecuteAsync(Ct)).Value.ShouldBe([Team]);
        (await new GetTeam(_teams).ExecuteAsync(TeamId, Ct)).Value.ShouldBe(Team);
        (await new ListTeamMembers(_members).ExecuteAsync(TeamId, Ct)).Value.ShouldBe([Bob]);
        (await new GetTeamMember(_members).ExecuteAsync(TeamId, Bob.Id, Ct)).Value.ShouldBe(Bob);
        (await new ListChannels(_channels).ExecuteAsync(TeamId, Ct)).Value.ShouldBe([General]);
        (await new GetChannel(_channels).ExecuteAsync(TeamId, ChannelId, Ct)).Value.ShouldBe(General);
        (await new GetPrimaryChannel(_channels).ExecuteAsync(TeamId, Ct)).Value.ShouldBe(General);
    }

    [Fact]
    public async Task Creating_a_team_checks_it_trims_its_name_and_reports_a_finished_operation()
    {
        var creation = new TeamCreation("  Probe ", "About", TeamVisibility.Public, [new ChatMemberDraft("bob-id", false)]);
        _teams.CreateAsync(Arg.Is<TeamCreation>(c => c.DisplayName == "Probe" && c.Members.Count == 1), Ct).Returns(Team);

        var operation = await new CreateTeam(_teams).ExecuteAsync(creation, Ct);

        operation.Value.ShouldBe(new TeamCreationOperation("create." + TeamId, TeamId, Created));
        (await new CreateTeam(_teams).ExecuteAsync(creation with { DisplayName = "" }, Ct)).Error!.Target.ShouldBe("displayName");
        (await new CreateTeam(_teams).ExecuteAsync(creation with { Description = new string('x', 2000) }, Ct)).Error!.Target.ShouldBe("description");
        await _teams.Received(1).CreateAsync(Arg.Any<TeamCreation>(), Ct);
    }

    [Fact]
    public async Task A_creation_failure_is_passed_on()
    {
        _teams.CreateAsync(Arg.Any<TeamCreation>(), Ct).Returns(Error.NotFound("nobody"));

        (await new CreateTeam(_teams).ExecuteAsync(new TeamCreation("Probe", null, null, []), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task An_operation_is_read_from_its_team_and_must_name_it()
    {
        _teams.GetAsync(TeamId, Ct).Returns(Team);

        var operation = await new GetTeamOperation(_teams).ExecuteAsync(TeamId, "create." + TeamId, Ct);
        operation.Value.CreatedDateTime.ShouldBe(Created);
        (await new GetTeamOperation(_teams).ExecuteAsync(TeamId, "create.otherteamotherteamotherteam12", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await new GetTeamOperation(_teams).ExecuteAsync(TeamId, "nonsense", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _teams.GetAsync("otherteamotherteamotherteam1234", Ct).Returns(Error.NotFound("gone"));
        (await new GetTeamOperation(_teams).ExecuteAsync("otherteamotherteamotherteam1234", "create.otherteamotherteamotherteam1234", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Updating_a_team_checks_it_and_an_empty_change_does_nothing()
    {
        _teams.UpdateAsync(TeamId, Arg.Any<TeamUpdate>(), Ct).Returns(Result.Success());

        (await new UpdateTeam(_teams).ExecuteAsync(TeamId, new TeamUpdate(null, null, null), Ct)).IsSuccess.ShouldBeTrue();
        await _teams.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, Ct);
        (await new UpdateTeam(_teams).ExecuteAsync(TeamId, new TeamUpdate(" ", null, null), Ct)).Error!.Target.ShouldBe("displayName");
        (await new UpdateTeam(_teams).ExecuteAsync(TeamId, new TeamUpdate(" New ", null, TeamVisibility.Public), Ct)).IsSuccess.ShouldBeTrue();
        await _teams.Received(1).UpdateAsync(TeamId, new TeamUpdate("New", null, TeamVisibility.Public), Ct);
    }

    [Fact]
    public async Task Only_a_team_id_is_deleted_any_other_group_is_unsupported()
    {
        _teams.DeleteAsync(TeamId, Ct).Returns(Result.Success());

        (await new DeleteTeam(_teams).ExecuteAsync(TeamId, Ct)).IsSuccess.ShouldBeTrue();
        (await new DeleteTeam(_teams).ExecuteAsync("5d3f2a1b-9c4e-4b7a-8f0d-2e6c1a9b7d44", Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        await _teams.Received(1).DeleteAsync(Arg.Any<string>(), Ct);
    }

    [Fact]
    public async Task Member_changes_pass_through()
    {
        var draft = new ChatMemberDraft("bob-id", true);
        _members.AddAsync(TeamId, draft, Ct).Returns(Bob);
        _members.SetOwnerAsync(TeamId, Bob.Id, true, Ct).Returns(Bob with { Roles = [ChatMember.OwnerRole] });
        _members.RemoveAsync(TeamId, Bob.Id, Ct).Returns(Result.Success());

        (await new AddTeamMember(_members).ExecuteAsync(TeamId, draft, Ct)).Value.ShouldBe(Bob);
        (await new UpdateTeamMember(_members).ExecuteAsync(TeamId, Bob.Id, true, Ct)).Value.Roles.ShouldBe([ChatMember.OwnerRole]);
        (await new RemoveTeamMember(_members).ExecuteAsync(TeamId, Bob.Id, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Channels_are_checked_before_they_are_made_or_changed()
    {
        _channels.CreateAsync(TeamId, new ChannelCreation("Design", null), Ct).Returns(new Channel("hktd6xvp", "Design"));
        _channels.UpdateAsync(TeamId, ChannelId, Arg.Any<ChannelUpdate>(), Ct).Returns(Result.Success());
        _channels.DeleteAsync(TeamId, ChannelId, Ct).Returns(Result.Success());

        (await new CreateChannel(_channels).ExecuteAsync(TeamId, new ChannelCreation(" Design ", null), Ct)).Value.Id.ShouldBe("hktd6xvp");
        (await new CreateChannel(_channels).ExecuteAsync(TeamId, new ChannelCreation("a/b", null), Ct)).Error!.Target.ShouldBe("displayName");
        (await new UpdateChannel(_channels).ExecuteAsync(TeamId, ChannelId, new ChannelUpdate(null, null), Ct)).IsSuccess.ShouldBeTrue();
        await _channels.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default!, Ct);
        (await new UpdateChannel(_channels).ExecuteAsync(TeamId, ChannelId, new ChannelUpdate("x?", null), Ct)).Error!.Target.ShouldBe("displayName");
        (await new UpdateChannel(_channels).ExecuteAsync(TeamId, ChannelId, new ChannelUpdate(" Ideas ", "d"), Ct)).IsSuccess.ShouldBeTrue();
        await _channels.Received(1).UpdateAsync(TeamId, ChannelId, new ChannelUpdate("Ideas", "d"), Ct);
        (await new DeleteChannel(_channels).ExecuteAsync(TeamId, ChannelId, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_channels_members_are_its_teams_once_the_channel_is_found()
    {
        _channels.GetAsync(TeamId, ChannelId, Ct).Returns(General);
        _members.ListAsync(TeamId, Ct).Returns(Result<IReadOnlyList<ChatMember>>.Success([Bob]));

        (await new ListChannelMembers(_channels, _members).ExecuteAsync(TeamId, ChannelId, Ct)).Value.ShouldBe([Bob]);

        _channels.GetAsync(TeamId, "nochannel", Ct).Returns(Error.NotFound("no"));
        (await new ListChannelMembers(_channels, _members).ExecuteAsync(TeamId, "nochannel", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task The_files_folder_is_read_from_the_drive_when_it_exists()
    {
        var folder = new DriveItem("501", "General") { IsFolder = true };
        _channels.FilesFolderPathAsync(TeamId, ChannelId, Ct).Returns(Result<IReadOnlyList<string>>.Success(["Probe", "General"]));
        _drive.GetItemAsync(Arg.Is<DriveItemRef>(r => r.ItemId == null && r.Path.SequenceEqual(new[] { "Probe", "General" })), Ct).Returns(folder);

        (await new GetChannelFilesFolder(_channels, _drive).ExecuteAsync(TeamId, ChannelId, Ct)).Value.ShouldBe(folder);
        await _drive.DidNotReceiveWithAnyArgs().CreateFolderAsync(default!, default!, default, Ct);
    }

    [Fact]
    public async Task The_files_folder_is_made_on_first_use()
    {
        var folder = new DriveItem("501", "General") { IsFolder = true };
        _channels.FilesFolderPathAsync(TeamId, ChannelId, Ct).Returns(Result<IReadOnlyList<string>>.Success(["Probe", "General"]));
        _drive.GetItemAsync(Arg.Any<DriveItemRef>(), Ct).Returns(Error.NotFound("none"));
        _drive.CreateFolderAsync(Arg.Is<DriveItemRef>(r => r.ItemId == null && r.Path.SequenceEqual(new[] { "Probe" })), "General", ConflictBehavior.Fail, Ct).Returns(folder);

        (await new GetChannelFilesFolder(_channels, _drive).ExecuteAsync(TeamId, ChannelId, Ct)).Value.ShouldBe(folder);
    }

    [Fact]
    public async Task A_folder_someone_else_made_in_between_is_read_again()
    {
        var folder = new DriveItem("501", "General") { IsFolder = true };
        _channels.FilesFolderPathAsync(TeamId, ChannelId, Ct).Returns(Result<IReadOnlyList<string>>.Success(["Probe", "General"]));
        _drive.GetItemAsync(Arg.Any<DriveItemRef>(), Ct).Returns(Error.NotFound("none"), folder);
        _drive.CreateFolderAsync(Arg.Any<DriveItemRef>(), "General", ConflictBehavior.Fail, Ct).Returns(Error.Conflict("taken"));

        (await new GetChannelFilesFolder(_channels, _drive).ExecuteAsync(TeamId, ChannelId, Ct)).Value.ShouldBe(folder);
    }

    [Fact]
    public async Task The_files_folder_fails_with_the_channel_the_drive_or_an_unusable_path()
    {
        _channels.FilesFolderPathAsync(TeamId, "nochannel", Ct).Returns(Error.NotFound("no"));
        (await new GetChannelFilesFolder(_channels, _drive).ExecuteAsync(TeamId, "nochannel", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _channels.FilesFolderPathAsync(TeamId, "badpath1", Ct).Returns(Result<IReadOnlyList<string>>.Success([".."]));
        (await new GetChannelFilesFolder(_channels, _drive).ExecuteAsync(TeamId, "badpath1", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);

        _channels.FilesFolderPathAsync(TeamId, ChannelId, Ct).Returns(Result<IReadOnlyList<string>>.Success(["Probe", "General"]));
        _drive.GetItemAsync(Arg.Any<DriveItemRef>(), Ct).Returns(Error.BackendUnavailable("down"));
        (await new GetChannelFilesFolder(_channels, _drive).ExecuteAsync(TeamId, ChannelId, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        await _drive.DidNotReceiveWithAnyArgs().CreateFolderAsync(default!, default!, default, Ct);
    }
}
