using Nightware.Application.Administration;
using Nightware.Application.Administration.SharedMailboxes;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class SharedMailboxSyncTests
{
    private readonly AdminTestBed _bed = new();

    private static CancellationToken Ct => AdminTestBed.Ct;

    private static readonly IdentityAccount Carol = new("u-carol", "carol", "carol@acme.example", true);

    private static readonly IdentityAccount Dave = new("u-dave", "dave", "Dave@acme.example", true);

    private static readonly IdentityAccount Erin = new("u-erin", "erin", "erin@acme.example", false);

    internal static SharedMailbox Support(params string[] aliases) =>
        new("s-sup", AdminTestBed.Acme, AdminTestBed.Address("support@acme.example"), "Support") { Aliases = [.. aliases.Select(AdminTestBed.Address)] };

    private SharedMailAccount OnServer(params SharedMailMember[] members)
    {
        var account = new SharedMailAccount("sm1", "support@acme.example") { Name = "Support", Members = members };
        _bed.SharedMail.FindByAddressAsync(AdminTestBed.Address("support@acme.example"), Arg.Any<CancellationToken>()).Returns(account);
        return account;
    }

    [Fact]
    public void Only_enabled_members_open_the_mailbox()
    {
        SharedMailboxSync.MemberAddresses([Erin, Dave, Carol]).ShouldBe(["carol@acme.example", "dave@acme.example"]);
    }

    [Fact]
    public async Task Apply_creates_a_missing_mailbox_named_for_its_sender_and_gives_members_their_own_mailbox_s_access()
    {
        _bed.AddMailbox("carol@acme.example", "m-carol");

        SharedMailboxSyncOutcome outcome = (await _bed.SharedMailboxSync().ApplyAsync(Support("help@acme.example"), [Carol, Erin], Ct)).Value;

        outcome.Notes.ShouldBeEmpty();
        await _bed.SharedMail.Received(1).CreateAsync(
            AdminTestBed.Address("support@acme.example"),
            Arg.Is<IReadOnlyList<MailAddress>>(a => a.Single().Value == "help@acme.example"),
            "Support",
            Arg.Any<CancellationToken>());
        await _bed.SharedMail.Received(1).AddMemberAsync("sm-new", "m-carol", Arg.Any<CancellationToken>());
        await _bed.SharedMail.ReceivedWithAnyArgs(1).AddMemberAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Apply_adds_and_removes_members_and_leaves_an_in_step_mailbox_alone()
    {
        _bed.AddMailbox("dave@acme.example", "m-dave");
        OnServer(new SharedMailMember("m-carol", "CAROL@acme.example"), new SharedMailMember("m-erin", "erin@acme.example"), new SharedMailMember("m-x", "x@acme.example"));

        (await _bed.SharedMailboxSync().ApplyAsync(Support(), [Carol, Dave, Erin], Ct)).IsSuccess.ShouldBeTrue();

        await _bed.SharedMail.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default!, Ct);
        await _bed.SharedMail.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default!, Ct);
        await _bed.SharedMail.Received(1).AddMemberAsync("sm1", "m-dave", Arg.Any<CancellationToken>());
        await _bed.SharedMail.Received(1).RemoveMemberAsync("sm1", "m-erin", Arg.Any<CancellationToken>());
        await _bed.SharedMail.Received(1).RemoveMemberAsync("sm1", "m-x", Arg.Any<CancellationToken>());
        await _bed.SharedMail.ReceivedWithAnyArgs(2).RemoveMemberAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Apply_renames_and_sets_aliases_when_they_differ()
    {
        OnServer();

        (await _bed.SharedMailboxSync().ApplyAsync(Support("help@acme.example") with { Name = "Help desk" }, [], Ct)).IsSuccess.ShouldBeTrue();

        await _bed.SharedMail.Received(1).UpdateAsync("sm1", Arg.Is<IReadOnlyList<MailAddress>>(a => a.Count == 1), "Help desk", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_member_without_a_mailbox_of_their_own_is_a_note_not_a_failure()
    {
        OnServer();

        SharedMailboxSyncOutcome outcome = (await _bed.SharedMailboxSync().ApplyAsync(Support(), [Carol], Ct)).Value;

        outcome.Notes.ShouldHaveSingleItem().ShouldContain("carol@acme.example has no mailbox of their own yet");
        await _bed.SharedMail.DidNotReceiveWithAnyArgs().AddMemberAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_privileged_account_at_the_address_is_never_given_members()
    {
        _bed.AddMailbox("carol@acme.example", "m-carol");
        _bed.SharedMail.FindByAddressAsync(default!, Ct).ReturnsForAnyArgs(new SharedMailAccount("sm1", "support@acme.example") { IsPrivileged = true });

        Error error = (await _bed.SharedMailboxSync().ApplyAsync(Support(), [Carol], Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Conflict);
        await _bed.SharedMail.DidNotReceiveWithAnyArgs().AddMemberAsync(default!, default!, Ct);
        (await _bed.SharedMailboxSync().InspectAsync(Support(), [Carol], Ct)).Single(l => l.Backend == BackendKind.Mail).State.ShouldBe(LinkState.Conflict);
    }

    [Fact]
    public async Task Inspect_shows_missing_in_step_and_out_of_step()
    {
        var sync = _bed.SharedMailboxSync();
        (await sync.InspectAsync(Support(), [Carol], Ct)).Single(l => l.Backend == BackendKind.Mail).State.ShouldBe(LinkState.Missing);

        // Carol has no mailbox of her own yet: waiting, not out of step.
        OnServer();
        (await sync.InspectAsync(Support(), [Carol], Ct)).Single(l => l.Backend == BackendKind.Mail).State.ShouldBe(LinkState.Linked);

        _bed.AddMailbox("carol@acme.example", "m-carol");
        BackendLink drift = (await sync.InspectAsync(Support(), [Carol], Ct)).Single(l => l.Backend == BackendKind.Mail);
        drift.State.ShouldBe(LinkState.Conflict);
        drift.Detail!.ShouldContain("members to add: carol@acme.example");

        OnServer(new SharedMailMember("m-carol", "carol@acme.example"));
        IReadOnlyList<BackendLink> links = await sync.InspectAsync(Support("help@acme.example"), [Carol], Ct);
        links.Single(l => l.Backend == BackendKind.Mail).Detail!.ShouldContain("aliases to add: help@acme.example");
        links.Single(l => l.Backend == BackendKind.Identity).Detail.ShouldBe("1 member(s).");
    }

    [Fact]
    public async Task Apply_for_a_user_brings_each_of_their_shared_mailboxes_into_line()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example", enabled: false);
        _bed.Mailboxes.ListUserMailboxesAsync(AdminTestBed.Acme, "u-carol", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<SharedMailbox>>.Success([Support()]));
        _bed.Mailboxes.ListMembersAsync(AdminTestBed.Acme, "s-sup", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([Carol with { Enabled = false }]));
        OnServer(new SharedMailMember("m-carol", "carol@acme.example"));

        (await _bed.SharedMailboxSync().ApplyForUserAsync("u-carol", Ct)).IsSuccess.ShouldBeTrue();
        await _bed.SharedMail.Received(1).RemoveMemberAsync("sm1", "m-carol", Arg.Any<CancellationToken>());

        _bed.Mailboxes.ListUserMailboxesAsync(AdminTestBed.Acme, "u-carol", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("keycloak down"));
        (await _bed.SharedMailboxSync().ApplyForUserAsync("u-carol", Ct)).Error!.Message.ShouldBe("Shared mailboxes: keycloak down");
    }

    [Fact]
    public async Task Apply_gives_the_calendar_a_members_group_and_shares_it_with_them()
    {
        OnServer();

        (await _bed.SharedMailboxSync().ApplyAsync(Support(), [Carol, Dave, Erin], Ct)).IsSuccess.ShouldBeTrue();

        await _bed.GroupwareGroups.Received(1).CreateAsync("mbx-support@acme.example", "Support", Arg.Any<CancellationToken>());
        await _bed.GroupwareGroups.Received(1).AddMemberAsync("mbx-support@acme.example", "carol", Arg.Any<CancellationToken>());
        await _bed.GroupwareGroups.Received(1).AddMemberAsync("mbx-support@acme.example", "dave", Arg.Any<CancellationToken>());
        await _bed.GroupwareGroups.DidNotReceive().AddMemberAsync("mbx-support@acme.example", "erin", Arg.Any<CancellationToken>());
        await _bed.SharedCalendars.Received(1).EnsureAsync(AdminTestBed.Address("support@acme.example"), "Support", "mbx-support@acme.example", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_takes_a_former_member_out_of_the_calendar_group_and_notes_one_not_yet_in_nextcloud()
    {
        OnServer();
        _bed.GroupwareGroups.FindAsync("mbx-support@acme.example", Arg.Any<CancellationToken>()).Returns(new GroupwareGroup("mbx-support@acme.example", "Support", ["dave", "zed"]));
        _bed.GroupwareGroups.AddMemberAsync("mbx-support@acme.example", "carol", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no user"));

        var outcome = (await _bed.SharedMailboxSync().ApplyAsync(Support(), [Carol, Dave], Ct)).Value;

        await _bed.GroupwareGroups.Received(1).RemoveMemberAsync("mbx-support@acme.example", "zed", Arg.Any<CancellationToken>());
        outcome.Notes.ShouldContain(n => n.Contains("carol") && n.Contains("calendar"));
    }

    [Fact]
    public async Task A_calendar_failure_fails_the_apply_naming_the_calendar()
    {
        OnServer();
        _bed.SharedCalendars.EnsureAsync(Arg.Any<MailAddress>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("nextcloud down"));

        (await _bed.SharedMailboxSync().ApplyAsync(Support(), [Carol], Ct)).Error!.Message.ShouldStartWith("Calendar:");
    }

    [Fact]
    public async Task Inspect_reports_the_calendar_missing_linked_or_out_of_step()
    {
        OnServer();
        (await _bed.SharedMailboxSync().InspectAsync(Support(), [Carol], Ct)).Single(l => l.Backend == BackendKind.Groupware).State.ShouldBe(LinkState.Missing);

        _bed.SharedCalendars.FindAsync(AdminTestBed.Address("support@acme.example"), Arg.Any<CancellationToken>()).Returns(new SharedCalendarAccount("mbx-support@acme.example", "Support"));
        _bed.GroupwareGroups.FindAsync("mbx-support@acme.example", Arg.Any<CancellationToken>()).Returns(new GroupwareGroup("mbx-support@acme.example", "Support", ["carol"]));
        (await _bed.SharedMailboxSync().InspectAsync(Support(), [Carol], Ct)).Single(l => l.Backend == BackendKind.Groupware).State.ShouldBe(LinkState.Linked);

        _bed.GroupwareGroups.FindAsync("mbx-support@acme.example", Arg.Any<CancellationToken>()).Returns(new GroupwareGroup("mbx-support@acme.example", "Support", ["carol", "mallory"]));
        (await _bed.SharedMailboxSync().InspectAsync(Support(), [Carol], Ct)).Single(l => l.Backend == BackendKind.Groupware).State.ShouldBe(LinkState.Conflict);
    }
}
