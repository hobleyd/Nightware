using Nightware.Application.Administration;
using Nightware.Application.Administration.Groups;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class GroupSyncTests
{
    private readonly AdminTestBed _bed = new();

    private static CancellationToken Ct => AdminTestBed.Ct;

    private static readonly IdentityAccount Carol = new("u-carol", "carol", "carol@acme.example", true) { Aliases = ["c.jones@acme.example"] };

    private static readonly IdentityAccount Dave = new("u-dave", "dave", "Dave@acme.example", true);

    private static readonly IdentityAccount Erin = new("u-erin", "erin", "erin@acme.example", false);

    private static Group Finance(SenderPolicy senders = SenderPolicy.Members, bool files = true, bool teamFolder = false, int? folderId = null) =>
        new("g-fin", AdminTestBed.Acme, Key("finance"), "Finance")
        {
            Mail = new GroupMail(AdminTestBed.Address("finance@acme.example"), senders)
            {
                ExternalRecipients = senders == SenderPolicy.Anyone ? [] : [Recipient("accountant@example.net")],
            },
            Files = files ? new GroupFiles(teamFolder) { TeamFolderId = folderId } : null,
        };

    [Fact]
    public void A_list_delivers_to_enabled_members_and_outside_recipients()
    {
        GroupSync.Recipients(Finance().Mail!, [Carol, Dave, Erin]).ShouldBe(["accountant@example.net", "carol@acme.example", "dave@acme.example"]);
        GroupSync.MemberSenders([Carol, Dave, Erin]).ShouldBe(["c.jones@acme.example", "carol@acme.example", "dave@acme.example"]);
        GroupSync.GroupwareMembers([Erin, Dave, Carol]).ShouldBe(["carol", "dave"]);
    }

    [Fact]
    public async Task Apply_creates_the_list_and_its_sender_rule_when_missing()
    {
        Result<GroupSyncOutcome> outcome = await _bed.GroupSync().ApplyAsync(AdminTestBed.AcmeEntity, Finance(files: false), [Carol, Erin], Ct);

        outcome.IsSuccess.ShouldBeTrue();
        await _bed.Lists.Received(1).CreateAsync(
            AdminTestBed.Address("finance@acme.example"),
            Arg.Is<IReadOnlyList<MailAddress>>(a => a.Count == 0),
            Arg.Is<IReadOnlyList<string>>(r => r.SequenceEqual(new[] { "accountant@example.net", "carol@acme.example" })),
            "Acme: Finance",
            Arg.Any<CancellationToken>());
        await _bed.Lists.Received(1).SetSenderRuleAsync(
            AdminTestBed.Address("finance@acme.example"),
            Arg.Is<SenderRule?>(r => r != null && r.AllowedSenders.SequenceEqual(new[] { "c.jones@acme.example", "carol@acme.example" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_updates_a_list_that_is_out_of_step_and_leaves_one_in_step_alone()
    {
        Group group = Finance(files: false);
        _bed.Lists.FindByAddressAsync(group.Mail!.Address, Arg.Any<CancellationToken>()).Returns(new MailingList("l1", "finance@acme.example") { Recipients = ["carol@acme.example"] });

        (await _bed.GroupSync().ApplyAsync(AdminTestBed.AcmeEntity, group, [Carol], Ct)).IsSuccess.ShouldBeTrue();
        await _bed.Lists.Received(1).UpdateAsync("l1", Arg.Any<IReadOnlyList<MailAddress>>(), Arg.Is<IReadOnlyList<string>>(r => r.Contains("accountant@example.net")), Arg.Any<CancellationToken>());

        _bed.Lists.ClearReceivedCalls();
        _bed.Lists.FindByAddressAsync(group.Mail.Address, Arg.Any<CancellationToken>()).Returns(new MailingList("l1", "finance@acme.example") { Recipients = ["CAROL@acme.example", "accountant@example.net"] });
        (await _bed.GroupSync().ApplyAsync(AdminTestBed.AcmeEntity, group, [Carol], Ct)).IsSuccess.ShouldBeTrue();
        await _bed.Lists.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task A_list_open_to_anyone_has_its_sender_rule_removed()
    {
        await _bed.GroupSync().ApplyAsync(AdminTestBed.AcmeEntity, Finance(SenderPolicy.Anyone, files: false), [Carol], Ct);
        await _bed.Lists.Received(1).SetSenderRuleAsync(AdminTestBed.Address("finance@acme.example"), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_sender_rule_is_written_even_when_the_list_is_not()
    {
        _bed.Lists.CreateAsync(default!, default!, default!, default!, Ct).ReturnsForAnyArgs(Error.Conflict("taken"));

        Result<GroupSyncOutcome> outcome = await _bed.GroupSync().ApplyAsync(AdminTestBed.AcmeEntity, Finance(files: false), [Carol], Ct);

        outcome.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        await _bed.Lists.ReceivedWithAnyArgs(1).SetSenderRuleAsync(default!, default, Ct);
    }

    [Fact]
    public async Task Apply_builds_the_Nextcloud_group_and_notes_members_not_signed_in_yet()
    {
        _bed.GroupwareGroups.AddMemberAsync("acme-finance", "dave", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no user"));

        GroupSyncOutcome outcome = (await _bed.GroupSync().ApplyAsync(AdminTestBed.AcmeEntity, Finance(), [Carol, Dave, Erin], Ct)).Value;

        await _bed.GroupwareGroups.Received(1).CreateAsync("acme-finance", "Finance", Arg.Any<CancellationToken>());
        await _bed.GroupwareGroups.Received(1).AddMemberAsync("acme-finance", "carol", Arg.Any<CancellationToken>());
        await _bed.GroupwareGroups.DidNotReceive().AddMemberAsync("acme-finance", "erin", Arg.Any<CancellationToken>());
        outcome.Notes.ShouldHaveSingleItem().ShouldContain("dave");
    }

    [Fact]
    public async Task Apply_takes_out_of_the_Nextcloud_group_who_does_not_belong()
    {
        _bed.GroupwareGroups.FindAsync("acme-finance", Arg.Any<CancellationToken>()).Returns(new GroupwareGroup("acme-finance", "Old name", ["carol", "erin", "mallory"]));

        (await _bed.GroupSync().ApplyAsync(AdminTestBed.AcmeEntity, Finance(), [Carol, Erin], Ct)).IsSuccess.ShouldBeTrue();

        await _bed.GroupwareGroups.Received(1).SetDisplayNameAsync("acme-finance", "Finance", Arg.Any<CancellationToken>());
        await _bed.GroupwareGroups.Received(1).RemoveMemberAsync("acme-finance", "erin", Arg.Any<CancellationToken>());
        await _bed.GroupwareGroups.Received(1).RemoveMemberAsync("acme-finance", "mallory", Arg.Any<CancellationToken>());
        await _bed.GroupwareGroups.DidNotReceiveWithAnyArgs().AddMemberAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_team_folder_is_created_recorded_then_shared()
    {
        GroupSyncOutcome outcome = (await _bed.GroupSync().ApplyAsync(AdminTestBed.AcmeEntity, Finance(teamFolder: true), [Carol], Ct)).Value;

        outcome.Group.Files!.TeamFolderId.ShouldBe(7);
        Received.InOrder(() =>
        {
            _bed.GroupwareGroups.CreateFolderAsync("Finance", Arg.Any<CancellationToken>());
            _bed.Groups.UpdateAsync(Arg.Is<Group>(g => g.Files!.TeamFolderId == 7), Arg.Any<CancellationToken>());
            _bed.GroupwareGroups.GrantFolderAsync(7, "acme-finance", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task A_team_folder_already_shared_is_left_alone()
    {
        _bed.GroupwareGroups.FindFolderAsync(3, Arg.Any<CancellationToken>()).Returns(new TeamFolder(3, "Finance", new HashSet<string> { "acme-finance" }));

        (await _bed.GroupSync().ApplyAsync(AdminTestBed.AcmeEntity, Finance(teamFolder: true, folderId: 3), [], Ct)).IsSuccess.ShouldBeTrue();

        await _bed.GroupwareGroups.DidNotReceiveWithAnyArgs().CreateFolderAsync(default!, Ct);
        await _bed.GroupwareGroups.DidNotReceiveWithAnyArgs().GrantFolderAsync(default, default!, Ct);
    }

    [Fact]
    public async Task Every_failure_is_reported_together()
    {
        _bed.Lists.FindByAddressAsync(default!, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("mail down"));
        _bed.GroupwareGroups.FindAsync(default!, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("cloud down"));

        Error error = (await _bed.GroupSync().ApplyAsync(AdminTestBed.AcmeEntity, Finance(), [Carol], Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        error.Message.ShouldContain("mail down");
        error.Message.ShouldContain("cloud down");
    }

    [Fact]
    public async Task Inspect_reports_each_facet()
    {
        IReadOnlyList<BackendLink> links = await _bed.GroupSync().InspectAsync(AdminTestBed.AcmeEntity, Finance(), [Carol], Ct);

        links.Select(static l => (l.Backend, l.State)).ShouldBe([(BackendKind.Identity, LinkState.Linked), (BackendKind.Mail, LinkState.Missing), (BackendKind.Groupware, LinkState.Missing)]);
        (await _bed.GroupSync().InspectAsync(AdminTestBed.AcmeEntity, Finance() with { Mail = null, Files = null }, [], Ct))
            .ShouldHaveSingleItem().Backend.ShouldBe(BackendKind.Identity);
    }

    [Fact]
    public async Task Inspect_names_what_is_out_of_step()
    {
        _bed.Lists.FindByAddressAsync(default!, Ct).ReturnsForAnyArgs(new MailingList("l1", "finance@acme.example") { Recipients = ["accountant@example.net", "mallory@acme.example"] });

        BackendLink mail = (await _bed.GroupSync().InspectAsync(AdminTestBed.AcmeEntity, Finance(files: false), [Carol], Ct)).Single(l => l.Backend == BackendKind.Mail);

        mail.State.ShouldBe(LinkState.Conflict);
        mail.Detail!.ShouldContain("recipients to add: carol@acme.example");
        mail.Detail!.ShouldContain("recipients to remove: mallory@acme.example");
        mail.DownstreamId.ShouldBe("l1");
    }

    [Fact]
    public async Task A_member_without_a_Nextcloud_account_is_not_out_of_step()
    {
        _bed.GroupwareGroups.FindAsync("acme-finance", Arg.Any<CancellationToken>()).Returns(new GroupwareGroup("acme-finance", "Finance", ["carol"]));
        _bed.AddCloudUser("carol", "carol@acme.example");

        BackendLink cloud = (await _bed.GroupSync().InspectAsync(AdminTestBed.AcmeEntity, Finance(), [Carol, Dave], Ct)).Single(l => l.Backend == BackendKind.Groupware);

        cloud.State.ShouldBe(LinkState.Linked);
    }

    [Fact]
    public async Task Inspect_reports_a_team_folder_not_shared_with_the_group()
    {
        _bed.GroupwareGroups.FindAsync("acme-finance", Arg.Any<CancellationToken>()).Returns(new GroupwareGroup("acme-finance", "Finance", []));
        _bed.GroupwareGroups.FindFolderAsync(3, Arg.Any<CancellationToken>()).Returns(new TeamFolder(3, "Finance", new HashSet<string>()));

        BackendLink cloud = (await _bed.GroupSync().InspectAsync(AdminTestBed.AcmeEntity, Finance(teamFolder: true, folderId: 3), [], Ct)).Single(l => l.Backend == BackendKind.Groupware);

        cloud.State.ShouldBe(LinkState.Conflict);
        cloud.Detail!.ShouldContain("not shared");
        (await _bed.GroupSync().InspectAsync(AdminTestBed.AcmeEntity, Finance(teamFolder: true), [], Ct)).Single(l => l.Backend == BackendKind.Groupware).State.ShouldBe(LinkState.Missing);
    }

    [Fact]
    public async Task A_user_s_groups_are_brought_into_line_in_each_of_their_entities()
    {
        _bed.Entities.GetMembershipsAsync("u-carol", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<string>>.Success([AdminTestBed.Acme]));
        _bed.Groups.ListUserGroupsAsync(AdminTestBed.Acme, "u-carol", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Group>>.Success([Finance(files: false)]));
        _bed.Groups.ListMembersAsync(AdminTestBed.Acme, "g-fin", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([Carol with { Enabled = false }, Dave]));

        (await _bed.GroupSync().ApplyForUserAsync("u-carol", Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Lists.Received(1).CreateAsync(Arg.Any<MailAddress>(), Arg.Any<IReadOnlyList<MailAddress>>(), Arg.Is<IReadOnlyList<string>>(r => !r.Contains("carol@acme.example") && r.Contains("dave@acme.example")), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_group_that_cannot_be_brought_into_line_for_a_user_is_named()
    {
        _bed.Entities.GetMembershipsAsync("u-carol", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<string>>.Success([AdminTestBed.Acme]));
        _bed.Groups.ListUserGroupsAsync(AdminTestBed.Acme, "u-carol", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Group>>.Success([Finance(files: false)]));
        _bed.Lists.FindByAddressAsync(default!, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("mail down"));

        Error error = (await _bed.GroupSync().ApplyForUserAsync("u-carol", Ct)).Error!;

        error.Message.ShouldStartWith("Groups: Finance: mail down");
    }

    internal static GroupKey Key(string value) => GroupKey.TryParse(value, out GroupKey? key) ? key : throw new ArgumentException(value);

    internal static RecipientAddress Recipient(string value) => RecipientAddress.TryParse(value, out RecipientAddress? r) ? r : throw new ArgumentException(value);
}
