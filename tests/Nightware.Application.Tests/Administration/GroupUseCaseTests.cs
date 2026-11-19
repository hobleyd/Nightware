using Nightware.Application.Administration;
using Nightware.Application.Administration.Groups;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class GroupUseCaseTests
{
    private readonly AdminTestBed _bed = new();

    private static CancellationToken Ct => AdminTestBed.Ct;

    private static GroupForm MailForm(string local = "finance", string domain = "acme.example") =>
        new("Finance") { Mail = true, LocalPart = local, Domain = domain };

    private Group ArrangeGroup(Group? group = null)
    {
        group ??= new Group("g-fin", AdminTestBed.Acme, GroupSyncTests.Key("finance"), "Finance")
        {
            Mail = new GroupMail(AdminTestBed.Address("finance@acme.example"), SenderPolicy.Members),
            Files = new GroupFiles(TeamFolder: false),
        };
        _bed.Groups.GetAsync(AdminTestBed.Acme, group.Id, Arg.Any<CancellationToken>()).Returns(group);
        return group;
    }

    private CreateGroup Create() => new(_bed.Actor, _bed.GroupAccess(), _bed.Groups, _bed.GroupSync(), _bed.Audit);

    private UpdateGroup Update() => new(_bed.Actor, _bed.GroupAccess(), _bed.Groups, _bed.Lists, _bed.GroupwareGroups, _bed.GroupSync(), _bed.Audit);

    private DeleteTeamFolder DeleteFolder() => new(_bed.Actor, _bed.GroupAccess(), _bed.Groups, _bed.GroupwareGroups, _bed.Audit, _bed.Options, _bed.Time);

    [Fact]
    public async Task Groups_are_listed_by_name_for_admins_of_the_entity_only()
    {
        _bed.Groups.ListAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Group>>.Success(
            [new Group("g2", AdminTestBed.Acme, GroupSyncTests.Key("sales"), "Sales"), new Group("g1", AdminTestBed.Acme, GroupSyncTests.Key("board"), "board")]));
        var list = new ListGroups(_bed.GroupAccess(), _bed.Groups);

        (await list.ExecuteAsync(AdminTestBed.Acme, Ct)).Value.Groups.Select(static g => g.Name).ShouldBe(["board", "Sales"]);
        _bed.ActAsEntityAdmin(AdminTestBed.Other);
        (await list.ExecuteAsync(AdminTestBed.Acme, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_group_detail_carries_members_and_links()
    {
        ArrangeGroup();
        _bed.Groups.ListMembersAsync(AdminTestBed.Acme, "g-fin", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([new IdentityAccount("u-carol", "carol", "carol@acme.example", true)]));

        GroupDetail detail = (await new GetGroup(_bed.GroupAccess()).ExecuteAsync(AdminTestBed.Acme, "g-fin", Ct)).Value;

        detail.Members.ShouldHaveSingleItem().Username.ShouldBe("carol");
        detail.For(BackendKind.Mail)!.State.ShouldBe(LinkState.Missing);
        detail.Entity.ShouldBe(AdminTestBed.AcmeEntity);
    }

    [Fact]
    public async Task Creating_a_group_proposes_its_key_builds_its_list_and_audits()
    {
        GroupSyncOutcome outcome = (await Create().ExecuteAsync(AdminTestBed.Acme, MailForm() with { Name = "Finance & Ops", Files = true }, Ct)).Value;

        await _bed.Groups.Received(1).CreateAsync(AdminTestBed.Acme, Arg.Is<NewGroup>(g => g.Key.Value == "finance-ops" && g.Mail!.Address.Value == "finance@acme.example" && g.Files != null), Arg.Any<CancellationToken>());
        await _bed.Lists.ReceivedWithAnyArgs(1).CreateAsync(default!, default!, default!, default!, Ct);
        await _bed.GroupwareGroups.Received(1).CreateAsync("acme-finance-ops", "Finance & Ops", Arg.Any<CancellationToken>());
        outcome.Group.Id.ShouldBe("g-new");
        AuditEvent audited = _bed.Audit.Single("group.created");
        audited.Succeeded.ShouldBeTrue();
        audited.Details["address"].ShouldBe("finance@acme.example");
    }

    [Theory]
    [InlineData("", "Give the group a name")]
    [InlineData("Finance", "lower-case letters")]
    public async Task A_group_needs_a_name_and_a_usable_key(string name, string message)
    {
        Error error = (await Create().ExecuteAsync(AdminTestBed.Acme, new GroupForm(name) { Key = name.Length > 0 ? "2bad" : null }, Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Message.ShouldContain(message);
        await _bed.Groups.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
    }

    [Theory]
    [InlineData("finance", "other.example", "not a domain of Acme")]
    [InlineData("postmaster", "acme.example", "reserved")]
    [InlineData("fin ance", "acme.example", "Enter the list's address")]
    public async Task A_list_address_must_be_the_entity_s_and_not_reserved(string local, string domain, string message)
    {
        Error error = (await Create().ExecuteAsync(AdminTestBed.Acme, MailForm(local, domain), Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Message.ShouldContain(message);
    }

    [Fact]
    public async Task A_list_address_held_by_a_user_mailbox_or_list_is_refused()
    {
        _bed.AddUser("u-fin", "fin", "finance@acme.example");
        (await Create().ExecuteAsync(AdminTestBed.Acme, MailForm(), Ct)).Error!.Message.ShouldContain("fin's address");

        _bed.AddMailbox("sales@acme.example");
        (await Create().ExecuteAsync(AdminTestBed.Acme, MailForm("sales"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);

        _bed.Lists.FindByAddressAsync(AdminTestBed.Address("board@acme.example"), Arg.Any<CancellationToken>()).Returns(new MailingList("l9", "board@acme.example"));
        (await Create().ExecuteAsync(AdminTestBed.Acme, MailForm("board"), Ct)).Error!.Message.ShouldContain("already a list's address");

        _bed.Mail.FindByAddressAsync("ops@acme.example", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("mail down"));
        (await Create().ExecuteAsync(AdminTestBed.Acme, MailForm("ops"), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        await _bed.Groups.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Outside_recipients_are_refused_on_a_list_open_to_anyone()
    {
        Error error = (await Create().ExecuteAsync(AdminTestBed.Acme, MailForm() with { Senders = SenderPolicy.Anyone, ExternalRecipients = ["accountant@example.net"] }, Ct)).Error!;

        error.Target.ShouldBe("senders");
    }

    [Theory]
    [InlineData("not-an-address", "is not an e-mail address")]
    [InlineData("carol@acme.example", "add that person as a member")]
    public async Task Outside_recipients_must_be_outside_addresses(string recipient, string message) =>
        (await Create().ExecuteAsync(AdminTestBed.Acme, MailForm() with { ExternalRecipients = [recipient] }, Ct)).Error!.Message.ShouldContain(message);

    [Fact]
    public async Task An_entity_admin_cannot_create_a_group_in_another_entity()
    {
        _bed.ActAsEntityAdmin(AdminTestBed.Other);
        (await Create().ExecuteAsync(AdminTestBed.Acme, new GroupForm("Finance"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_group_s_address_never_changes_but_its_aliases_can()
    {
        ArrangeGroup();

        GroupSyncOutcome outcome = (await Update().ExecuteAsync(AdminTestBed.Acme, "g-fin", MailForm("renamed") with { Name = "Money", Files = true, Aliases = ["accounts@acme.example"] }, Ct)).Value;

        outcome.Group.Name.ShouldBe("Money");
        outcome.Group.Mail!.Address.Value.ShouldBe("finance@acme.example");
        outcome.Group.Mail.Aliases.ShouldHaveSingleItem().Value.ShouldBe("accounts@acme.example");
        _bed.Audit.Single("group.updated").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Turning_the_mail_facet_off_destroys_the_list_and_opens_its_rule()
    {
        ArrangeGroup();
        _bed.Lists.FindByAddressAsync(AdminTestBed.Address("finance@acme.example"), Arg.Any<CancellationToken>()).Returns(new MailingList("l1", "finance@acme.example"));

        (await Update().ExecuteAsync(AdminTestBed.Acme, "g-fin", new GroupForm("Finance") { Files = true }, Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Lists.Received(1).DestroyAsync("l1", Arg.Any<CancellationToken>());
        await _bed.Lists.Received(1).SetSenderRuleAsync(AdminTestBed.Address("finance@acme.example"), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_list_that_cannot_be_taken_down_leaves_the_group_as_it_was()
    {
        ArrangeGroup();
        _bed.Lists.FindByAddressAsync(AdminTestBed.Address("finance@acme.example"), Arg.Any<CancellationToken>()).Returns(new MailingList("l1", "finance@acme.example"));
        _bed.Lists.DestroyAsync("l1", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("mail down"));

        (await Update().ExecuteAsync(AdminTestBed.Acme, "g-fin", new GroupForm("Finance") { Files = true }, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);

        await _bed.Groups.DidNotReceiveWithAnyArgs().UpdateAsync(default!, Ct);
        _bed.Audit.Single("group.updated").Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Turning_the_files_facet_off_deletes_the_Nextcloud_group()
    {
        ArrangeGroup();

        (await Update().ExecuteAsync(AdminTestBed.Acme, "g-fin", MailForm(), Ct)).IsSuccess.ShouldBeTrue();

        await _bed.GroupwareGroups.Received(1).DeleteAsync("acme-finance", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_team_folder_is_only_taken_away_by_deleting_it()
    {
        Group group = ArrangeGroup();
        ArrangeGroup(group with { Files = new GroupFiles(TeamFolder: true) { TeamFolderId = 3 } });

        Error error = (await Update().ExecuteAsync(AdminTestBed.Acme, "g-fin", MailForm() with { Files = true }, Ct)).Error!;

        error.Target.ShouldBe("teamFolder");
        await _bed.Groups.DidNotReceiveWithAnyArgs().UpdateAsync(default!, Ct);
    }

    [Fact]
    public async Task Only_a_user_of_the_entity_can_join_its_group()
    {
        ArrangeGroup();
        _bed.AddUser("u-zed", "zed", "zed@other.example", entities: [AdminTestBed.Other]);
        var add = new AddGroupMember(_bed.Actor, _bed.GroupAccess(), _bed.Entities, _bed.Groups, _bed.GroupSync(), _bed.Audit);

        (await add.ExecuteAsync(AdminTestBed.Acme, "g-fin", "u-zed", Ct)).Error!.Target.ShouldBe("userId");

        _bed.AddUser("u-carol", "carol", "carol@acme.example");
        (await add.ExecuteAsync(AdminTestBed.Acme, "g-fin", "u-carol", Ct)).IsSuccess.ShouldBeTrue();
        await _bed.Groups.Received(1).AddMemberAsync(AdminTestBed.Acme, "g-fin", "u-carol", Arg.Any<CancellationToken>());
        await _bed.Lists.ReceivedWithAnyArgs(1).SetSenderRuleAsync(default!, default, Ct);
        _bed.Audit.Single("group.member.added").TargetUserId.ShouldBe("u-carol");
    }

    [Fact]
    public async Task Removing_a_member_brings_the_backends_into_line()
    {
        ArrangeGroup();
        var remove = new RemoveGroupMember(_bed.Actor, _bed.GroupAccess(), _bed.Groups, _bed.GroupSync(), _bed.Audit);

        (await remove.ExecuteAsync(AdminTestBed.Acme, "g-fin", "u-carol", Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Groups.Received(1).RemoveMemberAsync(AdminTestBed.Acme, "g-fin", "u-carol", Arg.Any<CancellationToken>());
        await _bed.GroupwareGroups.ReceivedWithAnyArgs(1).FindAsync(default!, Ct);
    }

    [Fact]
    public async Task Repair_brings_the_backends_into_line_and_audits()
    {
        ArrangeGroup();

        (await new RepairGroup(_bed.Actor, _bed.GroupAccess(), _bed.Groups, _bed.GroupSync(), _bed.Audit).ExecuteAsync(AdminTestBed.Acme, "g-fin", Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Lists.ReceivedWithAnyArgs(1).CreateAsync(default!, default!, default!, default!, Ct);
        _bed.Audit.Single("group.repaired").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Deleting_a_group_takes_down_its_backends_before_the_record()
    {
        ArrangeGroup();
        _bed.Lists.FindByAddressAsync(default!, Ct).ReturnsForAnyArgs(new MailingList("l1", "finance@acme.example"));
        var delete = new DeleteGroup(_bed.Actor, _bed.GroupAccess(), _bed.Groups, _bed.Lists, _bed.GroupwareGroups, _bed.Audit);

        (await delete.ExecuteAsync(AdminTestBed.Acme, "g-fin", Ct)).IsSuccess.ShouldBeTrue();

        Received.InOrder(() =>
        {
            _bed.Lists.DestroyAsync("l1", Arg.Any<CancellationToken>());
            _bed.GroupwareGroups.DeleteAsync("acme-finance", Arg.Any<CancellationToken>());
            _bed.Groups.DeleteAsync(AdminTestBed.Acme, "g-fin", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task A_group_whose_backends_cannot_be_reached_is_not_deleted()
    {
        ArrangeGroup();
        _bed.GroupwareGroups.DeleteAsync(default!, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("cloud down"));

        (await new DeleteGroup(_bed.Actor, _bed.GroupAccess(), _bed.Groups, _bed.Lists, _bed.GroupwareGroups, _bed.Audit).ExecuteAsync(AdminTestBed.Acme, "g-fin", Ct))
            .Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        await _bed.Groups.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default!, Ct);
        _bed.Audit.Single("group.deleted").Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Deleting_a_team_folder_needs_a_global_admin_a_fresh_sign_in_and_the_name()
    {
        Group group = ArrangeGroup();
        ArrangeGroup(group with { Files = new GroupFiles(TeamFolder: true) { TeamFolderId = 3 } });

        _bed.ActAsEntityAdmin();
        (await DeleteFolder().ExecuteAsync(AdminTestBed.Acme, "g-fin", "Finance", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        _bed.ActAsGlobal();
        _bed.SignedInAgo(TimeSpan.FromMinutes(10));
        (await DeleteFolder().ExecuteAsync(AdminTestBed.Acme, "g-fin", "Finance", Ct)).Error!.Code.ShouldBe("reauthenticationRequired");

        _bed.ActAsGlobal();
        (await DeleteFolder().ExecuteAsync(AdminTestBed.Acme, "g-fin", "Sales", Ct)).Error!.Target.ShouldBe("confirmation");
        await _bed.GroupwareGroups.DidNotReceiveWithAnyArgs().DeleteFolderAsync(default, Ct);

        Group after = (await DeleteFolder().ExecuteAsync(AdminTestBed.Acme, "g-fin", " finance ", Ct)).Value;
        after.Files.ShouldBe(new GroupFiles(TeamFolder: false));
        await _bed.GroupwareGroups.Received(1).DeleteFolderAsync(3, Arg.Any<CancellationToken>());
        _bed.Audit.Single("group.folder.deleted").Details["folder"].ShouldBe("deleted");
    }

    [Fact]
    public async Task A_team_folder_already_gone_still_clears_the_record()
    {
        Group group = ArrangeGroup();
        ArrangeGroup(group with { Files = new GroupFiles(TeamFolder: true) { TeamFolderId = 3 } });
        _bed.GroupwareGroups.DeleteFolderAsync(3, Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));

        (await DeleteFolder().ExecuteAsync(AdminTestBed.Acme, "g-fin", "Finance", Ct)).IsSuccess.ShouldBeTrue();
        _bed.Audit.Single("group.folder.deleted").Details["folder"].ShouldBe("absent");
    }

    [Fact]
    public async Task A_group_without_a_team_folder_has_none_to_delete()
    {
        ArrangeGroup();
        (await DeleteFolder().ExecuteAsync(AdminTestBed.Acme, "g-fin", "Finance", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task A_user_s_groups_are_listed_in_the_entities_the_actor_manages()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example", entities: [AdminTestBed.Acme, AdminTestBed.Other]);
        _bed.Groups.ListUserGroupsAsync(AdminTestBed.Acme, "u-carol", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Group>>.Success([new Group("g1", AdminTestBed.Acme, GroupSyncTests.Key("finance"), "Finance")]));
        _bed.Groups.ListUserGroupsAsync(AdminTestBed.Other, "u-carol", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Group>>.Success([new Group("g2", AdminTestBed.Other, GroupSyncTests.Key("board"), "Board")]));
        var list = new ListUserGroups(_bed.Actor, _bed.UserAccess(), _bed.Entities, _bed.Groups);

        (await list.ExecuteAsync("u-carol", Ct)).Value.Select(static g => g.Name).ShouldBe(["Board", "Finance"]);
    }

    [Fact]
    public async Task Offboarding_takes_the_user_out_of_their_groups_and_reports_a_failure()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example");
        _bed.Entities.GetMembershipsAsync("u-carol", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<string>>.Failure(Error.BackendUnavailable("keycloak down")), Result<IReadOnlyList<string>>.Success([AdminTestBed.Acme]));

        var offboard = new OffboardUser(_bed.Actor, _bed.UserAccess(), _bed.Identities, _bed.Mail, _bed.Groupware, _bed.Audit, _bed.History(), _bed.GroupSync(), _bed.SharedMailboxSync());
        _bed.Entities.GetMembershipsAsync("u-carol", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<string>>.Success([AdminTestBed.Acme]));
        (await offboard.ExecuteAsync("u-carol", Ct)).IsSuccess.ShouldBeTrue();
        await _bed.Groups.Received().ListUserGroupsAsync(AdminTestBed.Acme, "u-carol", Arg.Any<CancellationToken>());

        _bed.Groups.ListUserGroupsAsync(AdminTestBed.Acme, "u-carol", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("keycloak down"));
        (await offboard.ExecuteAsync("u-carol", Ct)).Error!.Message.ShouldContain("Groups: keycloak down");
    }
}
