using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.SharedMailboxes;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class SharedMailboxUseCaseTests
{
    private readonly AdminTestBed _bed = new();

    private static CancellationToken Ct => AdminTestBed.Ct;

    private static SharedMailboxForm Form(string local = "support", string domain = "acme.example") =>
        new("Support") { LocalPart = local, Domain = domain };

    private SharedMailbox Arrange(SharedMailbox? mailbox = null)
    {
        mailbox ??= SharedMailboxSyncTests.Support();
        _bed.Mailboxes.GetAsync(AdminTestBed.Acme, mailbox.Id, Arg.Any<CancellationToken>()).Returns(mailbox);
        return mailbox;
    }

    private CreateSharedMailbox Create() => new(_bed.Actor, _bed.SharedMailboxAccess(), _bed.Mailboxes, _bed.SharedMailboxSync(), _bed.Audit);

    private UpdateSharedMailbox Update() => new(_bed.Actor, _bed.SharedMailboxAccess(), _bed.Mailboxes, _bed.SharedMailboxSync(), _bed.Audit);

    private DeleteSharedMailbox Delete() => new(_bed.Actor, _bed.SharedMailboxAccess(), _bed.Mailboxes, _bed.SharedMail, _bed.Mail, _bed.Audit, _bed.Options, _bed.Time);

    [Fact]
    public async Task Shared_mailboxes_are_listed_by_name_for_admins_of_the_entity_only()
    {
        _bed.Mailboxes.ListAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<SharedMailbox>>.Success(
            [SharedMailboxSyncTests.Support() with { Name = "support" }, SharedMailboxSyncTests.Support() with { Id = "s2", Name = "Accounts" }]));
        var list = new ListSharedMailboxes(_bed.SharedMailboxAccess(), _bed.Mailboxes);

        (await list.ExecuteAsync(AdminTestBed.Acme, Ct)).Value.Mailboxes.Select(static m => m.Name).ShouldBe(["Accounts", "support"]);
        _bed.ActAsEntityAdmin(AdminTestBed.Other);
        (await list.ExecuteAsync(AdminTestBed.Acme, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_detail_carries_members_and_links()
    {
        Arrange();
        _bed.Mailboxes.ListMembersAsync(AdminTestBed.Acme, "s-sup", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([new IdentityAccount("u-carol", "carol", "carol@acme.example", true)]));

        SharedMailboxDetail detail = (await new GetSharedMailbox(_bed.SharedMailboxAccess()).ExecuteAsync(AdminTestBed.Acme, "s-sup", Ct)).Value;

        detail.Members.ShouldHaveSingleItem().Username.ShouldBe("carol");
        detail.For(BackendKind.Mail)!.State.ShouldBe(LinkState.Missing);
        detail.Entity.ShouldBe(AdminTestBed.AcmeEntity);
    }

    [Fact]
    public async Task Creating_records_the_mailbox_builds_it_and_audits()
    {
        SharedMailboxSyncOutcome outcome = (await Create().ExecuteAsync(AdminTestBed.Acme, Form() with { Aliases = ["help@acme.example", " "] }, Ct)).Value;

        await _bed.Mailboxes.Received(1).CreateAsync(AdminTestBed.Acme, Arg.Is<NewSharedMailbox>(m => m.Address.Value == "support@acme.example" && m.Name == "Support" && m.Aliases.Single().Value == "help@acme.example"), Arg.Any<CancellationToken>());
        await _bed.SharedMail.ReceivedWithAnyArgs(1).CreateAsync(default!, default!, default!, Ct);
        outcome.Mailbox.Id.ShouldBe("s-new");
        AuditEvent audited = _bed.Audit.Single("mailbox.created");
        audited.Succeeded.ShouldBeTrue();
        audited.Details["address"].ShouldBe("support@acme.example");
    }

    [Theory]
    [InlineData("support", "other.example", "not a domain of Acme")]
    [InlineData("postmaster", "acme.example", "reserved")]
    [InlineData("sup port", "acme.example", "Enter the shared mailbox's address")]
    public async Task The_address_must_be_the_entity_s_and_not_reserved(string local, string domain, string message)
    {
        Error error = (await Create().ExecuteAsync(AdminTestBed.Acme, Form(local, domain), Ct)).Error!;

        error.Message.ShouldContain(message);
        await _bed.Mailboxes.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_shared_mailbox_needs_a_name()
    {
        Error error = (await Create().ExecuteAsync(AdminTestBed.Acme, Form() with { Name = " " }, Ct)).Error!;

        error.Target.ShouldBe("name");
    }

    [Fact]
    public async Task An_address_in_use_is_refused_naming_its_holder()
    {
        _bed.AddUser("u-carol", "carol", "support@acme.example");
        (await Create().ExecuteAsync(AdminTestBed.Acme, Form(), Ct)).Error!.Message.ShouldContain("is carol's address");

        _bed.AddMailbox("help@acme.example", "sm9", individual: false);
        _bed.Mail.FindByAddressAsync("help@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("sm9", "helpdesk@acme.example", false) { IsSharedMailbox = true });
        (await Create().ExecuteAsync(AdminTestBed.Acme, Form("help"), Ct)).Error!.Message.ShouldBe("help@acme.example already belongs to the shared mailbox helpdesk@acme.example.");
    }

    [Fact]
    public async Task Updating_keeps_the_address_and_checks_only_new_aliases()
    {
        SharedMailbox mailbox = Arrange(SharedMailboxSyncTests.Support("help@acme.example"));
        // Its own alias is in use (by itself): not a clash.
        _bed.AddMailbox("help@acme.example", "sm1", individual: false);

        SharedMailboxSyncOutcome outcome = (await Update().ExecuteAsync(AdminTestBed.Acme, mailbox.Id, new SharedMailboxForm("Help desk") { LocalPart = "ignored", Aliases = ["help@acme.example", "desk@acme.example"] }, Ct)).Value;

        outcome.Mailbox.Address.Value.ShouldBe("support@acme.example");
        await _bed.Mailboxes.Received(1).UpdateAsync(Arg.Is<SharedMailbox>(m => m.Name == "Help desk" && m.Aliases.Count == 2), Arg.Any<CancellationToken>());
        _bed.Audit.Single("mailbox.updated").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Only_a_user_of_the_entity_can_be_added()
    {
        Arrange();
        _bed.AddUser("u-zed", "zed", "zed@other.example", entities: [AdminTestBed.Other]);
        var add = new AddSharedMailboxMember(_bed.Actor, _bed.SharedMailboxAccess(), _bed.Entities, _bed.Mailboxes, _bed.SharedMailboxSync(), _bed.Audit);

        (await add.ExecuteAsync(AdminTestBed.Acme, "s-sup", "u-zed", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _bed.Mailboxes.DidNotReceiveWithAnyArgs().AddMemberAsync(default!, default!, default!, Ct);

        _bed.AddUser("u-carol", "carol", "carol@acme.example");
        (await add.ExecuteAsync(AdminTestBed.Acme, "s-sup", "u-carol", Ct)).IsSuccess.ShouldBeTrue();
        await _bed.Mailboxes.Received(1).AddMemberAsync(AdminTestBed.Acme, "s-sup", "u-carol", Arg.Any<CancellationToken>());
        _bed.Audit.Single("mailbox.member.added").TargetUserId.ShouldBe("u-carol");
    }

    [Fact]
    public async Task Removing_a_member_takes_their_access_away()
    {
        Arrange();
        _bed.SharedMail.FindByAddressAsync(default!, Ct).ReturnsForAnyArgs(new SharedMailAccount("sm1", "support@acme.example") { Name = "Support", Members = [new SharedMailMember("m-carol", "carol@acme.example")] });
        var remove = new RemoveSharedMailboxMember(_bed.Actor, _bed.SharedMailboxAccess(), _bed.Mailboxes, _bed.SharedMailboxSync(), _bed.Audit);

        (await remove.ExecuteAsync(AdminTestBed.Acme, "s-sup", "u-carol", Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Mailboxes.Received(1).RemoveMemberAsync(AdminTestBed.Acme, "s-sup", "u-carol", Arg.Any<CancellationToken>());
        await _bed.SharedMail.Received(1).RemoveMemberAsync("sm1", "m-carol", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Repair_builds_a_missing_mailbox_and_an_entity_admin_of_another_entity_cannot()
    {
        Arrange();
        var repair = new RepairSharedMailbox(_bed.Actor, _bed.SharedMailboxAccess(), _bed.Mailboxes, _bed.SharedMailboxSync(), _bed.Audit);

        (await repair.ExecuteAsync(AdminTestBed.Acme, "s-sup", Ct)).IsSuccess.ShouldBeTrue();
        await _bed.SharedMail.ReceivedWithAnyArgs(1).CreateAsync(default!, default!, default!, Ct);

        _bed.ActAsEntityAdmin(AdminTestBed.Other);
        (await repair.ExecuteAsync(AdminTestBed.Acme, "s-sup", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Deleting_destroys_the_mail_then_the_record_after_the_address_is_typed()
    {
        Arrange();
        _bed.SharedMail.FindByAddressAsync(default!, Ct).ReturnsForAnyArgs(new SharedMailAccount("sm1", "support@acme.example"));

        (await Delete().ExecuteAsync(AdminTestBed.Acme, "s-sup", "sales@acme.example", Ct)).Error!.Target.ShouldBe("confirmation");
        (await Delete().ExecuteAsync(AdminTestBed.Acme, "s-sup", null, Ct)).Error!.Target.ShouldBe("confirmation");
        await _bed.SharedMail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);

        (await Delete().ExecuteAsync(AdminTestBed.Acme, "s-sup", " Support@acme.example ", Ct)).IsSuccess.ShouldBeTrue();

        Received.InOrder(() =>
        {
            _bed.SharedMail.DestroyAsync("sm1", Arg.Any<CancellationToken>());
            _bed.Mailboxes.DeleteAsync(AdminTestBed.Acme, "s-sup", Arg.Any<CancellationToken>());
        });
        _bed.Audit.Events.Last().Details["mail"].ShouldBe("destroyed");
    }

    [Fact]
    public async Task A_failed_destroy_keeps_the_record_and_an_absent_mailbox_is_deleted_anyway()
    {
        Arrange();
        _bed.SharedMail.FindByAddressAsync(default!, Ct).ReturnsForAnyArgs(new SharedMailAccount("sm1", "support@acme.example"));
        _bed.SharedMail.DestroyAsync(default!, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("stalwart down"));

        (await Delete().ExecuteAsync(AdminTestBed.Acme, "s-sup", "support@acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        await _bed.Mailboxes.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default!, Ct);

        _bed.SharedMail.FindByAddressAsync(default!, Ct).ReturnsForAnyArgs(Error.NotFound("gone"));
        (await Delete().ExecuteAsync(AdminTestBed.Acme, "s-sup", "support@acme.example", Ct)).IsSuccess.ShouldBeTrue();
        _bed.Audit.Events.Last().Details["mail"].ShouldBe("absent");
    }

    [Fact]
    public async Task Deleting_needs_the_right_to_delete_data_and_a_fresh_sign_in()
    {
        SharedMailbox mailbox = Arrange();

        _bed.ActAsEntityAdmin();
        Delete().Check(mailbox, null).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _bed.Options.EntityAdminsMayDeleteData = true;
        Delete().Check(mailbox, null).IsSuccess.ShouldBeTrue();

        _bed.SignedInAgo(TimeSpan.FromHours(1));
        (await Delete().ExecuteAsync(AdminTestBed.Acme, "s-sup", "support@acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.Unauthorized);
        await _bed.SharedMail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);
    }

    [Fact]
    public async Task A_privileged_account_is_never_deleted()
    {
        Arrange();
        _bed.SharedMail.FindByAddressAsync(default!, Ct).ReturnsForAnyArgs(new SharedMailAccount("sm1", "support@acme.example") { IsPrivileged = true });

        (await Delete().ExecuteAsync(AdminTestBed.Acme, "s-sup", "support@acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
        await _bed.SharedMail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);
    }

    [Fact]
    public async Task Usage_is_the_mailbox_s_or_null_when_it_is_not_on_the_server()
    {
        SharedMailbox mailbox = Arrange();
        (await Delete().GetUsageAsync(mailbox, Ct)).Value.ShouldBeNull();

        _bed.SharedMail.FindByAddressAsync(default!, Ct).ReturnsForAnyArgs(new SharedMailAccount("sm1", "support@acme.example"));
        (await Delete().GetUsageAsync(mailbox, Ct)).Value.ShouldBe(1234);
    }

    [Fact]
    public async Task A_user_s_shared_mailboxes_are_listed_in_the_entities_the_actor_manages()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example", entities: [AdminTestBed.Acme, AdminTestBed.Other]);
        _bed.Mailboxes.ListUserMailboxesAsync(AdminTestBed.Acme, "u-carol", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<SharedMailbox>>.Success([SharedMailboxSyncTests.Support()]));
        _bed.Mailboxes.ListUserMailboxesAsync(AdminTestBed.Other, "u-carol", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<SharedMailbox>>.Success([SharedMailboxSyncTests.Support() with { Id = "s2", EntityId = AdminTestBed.Other, Name = "Accounts" }]));
        var list = new ListUserSharedMailboxes(_bed.Actor, _bed.UserAccess(), _bed.Entities, _bed.Mailboxes);

        (await list.ExecuteAsync("u-carol", Ct)).Value.Select(static m => m.Name).ShouldBe(["Accounts", "Support"]);
    }

    [Fact]
    public async Task Removing_a_domain_is_refused_while_a_shared_mailbox_has_an_address_on_it()
    {
        _bed.Entities.ListMembersAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([]));
        _bed.Mailboxes.ListAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<SharedMailbox>>.Success(
            [SharedMailboxSyncTests.Support("help@acme.org"), SharedMailboxSyncTests.Support() with { Id = "s2", Name = "Sales" }]));

        Error error = (await new RemoveEntityDomain(_bed.Actor, _bed.Entities, _bed.Groups, _bed.Mailboxes, _bed.MailDomains, _bed.Audit).ExecuteAsync(AdminTestBed.Acme, "acme.org", Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Conflict);
        error.Message.ShouldStartWith("The shared mailbox(es) Support have addresses on acme.org");
        await _bed.MailDomains.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);
    }

    [Fact]
    public async Task Offboarding_and_reinstating_bring_the_user_s_shared_mailboxes_into_line()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example");
        var offboard = new OffboardUser(_bed.Actor, _bed.UserAccess(), _bed.Identities, _bed.Mail, _bed.Groupware, _bed.Audit, _bed.History(), _bed.GroupSync(), _bed.SharedMailboxSync());

        (await offboard.ExecuteAsync("u-carol", Ct)).IsSuccess.ShouldBeTrue();
        await _bed.Mailboxes.Received(1).ListUserMailboxesAsync(AdminTestBed.Acme, "u-carol", Arg.Any<CancellationToken>());

        _bed.Mailboxes.ListUserMailboxesAsync(AdminTestBed.Acme, "u-carol", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("keycloak down"));
        (await offboard.ExecuteAsync("u-carol", Ct)).Error!.Message.ShouldContain("Shared mailboxes: keycloak down");

        _bed.Update(new IdentityAccount("u-carol", "carol", "carol@acme.example", false));
        var reinstate = new ReinstateUser(_bed.Actor, _bed.UserAccess(), _bed.Identities, _bed.Mail, _bed.Groupware, _bed.Audit, _bed.Options, _bed.Time, _bed.History(), _bed.GroupSync(), _bed.SharedMailboxSync());
        (await reinstate.ExecuteAsync("u-carol", false, Ct)).Error!.Message.ShouldContain("Shared mailboxes: keycloak down");
    }
}
