using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class CatchAllTests
{
    private const string Alice = "alice@acme.example";

    private static readonly MailDomain Acme = AdminTestBed.Domain("acme.example");

    private readonly AdminTestBed _bed = new();

    public CatchAllTests()
    {
        _bed.AddUser("u-alice", "alice", Alice);
        _bed.AddMailbox(Alice, "m-alice");
        _bed.Entities.SetCatchAllAsync(default!, default!, default, default).ReturnsForAnyArgs(AdminTestBed.AcmeEntity);
    }

    private static CancellationToken Ct => AdminTestBed.Ct;

    private SetDomainCatchAll Set => new(_bed.Actor, _bed.Entities, _bed.UserAccess(), _bed.MailDomains, _bed.Audit);

    private static Entity WithCatchAll(string userId) =>
        AdminTestBed.AcmeEntity with { CatchAlls = new Dictionary<MailDomain, string> { [Acme] = userId } };

    [Fact]
    public async Task An_entity_admin_sends_a_domains_mail_to_unknown_addresses_to_one_of_its_users()
    {
        _bed.ActAsEntityAdmin();

        MailAddress? address = (await Set.ExecuteAsync(AdminTestBed.Acme, "ACME.example", "u-alice", Ct)).Value;

        address!.Value.ShouldBe(Alice);
        Received.InOrder(() =>
        {
            _bed.Entities.SetCatchAllAsync(AdminTestBed.Acme, Acme, "u-alice", Arg.Any<CancellationToken>());
            _bed.MailDomains.SetCatchAllAsync(Acme, AdminTestBed.Address(Alice), Arg.Any<CancellationToken>());
        });
        AuditEvent audit = _bed.Audit.Single("entity.domain.catch-all");
        audit.Succeeded.ShouldBeTrue();
        audit.TargetUserId.ShouldBe("u-alice");
        audit.Details["address"].ShouldBe(Alice);
        audit.Details["mailServer"].ShouldBe("set");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task No_user_refuses_that_mail_again(string? userId)
    {
        (await Set.ExecuteAsync(AdminTestBed.Acme, "acme.example", userId, Ct)).Value.ShouldBeNull();

        await _bed.Entities.Received(1).SetCatchAllAsync(AdminTestBed.Acme, Acme, null, Arg.Any<CancellationToken>());
        await _bed.MailDomains.Received(1).SetCatchAllAsync(Acme, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_an_enabled_user_of_the_entity_with_their_own_mailbox_may_receive_it()
    {
        _bed.AddUser("u-off", "off", "off@acme.example", enabled: false);
        _bed.AddMailbox("off@acme.example", "m-off");
        _bed.AddUser("u-new", "new", "new@acme.example");
        _bed.AddUser("u-otto", "otto", "otto@other.example", entities: [AdminTestBed.Other]);
        _bed.AddMailbox("otto@other.example", "m-otto");

        (await Set.ExecuteAsync(AdminTestBed.Acme, "acme.example", "u-off", Ct)).Error!.Message.ShouldContain("offboarded");
        (await Set.ExecuteAsync(AdminTestBed.Acme, "acme.example", "u-new", Ct)).Error!.Message.ShouldContain("no mailbox");
        _bed.AddUser("u-al", "al", "al@acme.example");
        _bed.Mail.FindByAddressAsync("al@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-alice", Alice, true));
        (await Set.ExecuteAsync(AdminTestBed.Acme, "acme.example", "u-al", Ct)).Error!.Message.ShouldBe($"al's mailbox cannot receive it: al@acme.example is an alias of the mailbox {Alice}.");
        (await Set.ExecuteAsync(AdminTestBed.Acme, "acme.example", "u-otto", Ct)).Error!.Message.ShouldContain("not a user of this entity");
        (await Set.ExecuteAsync(AdminTestBed.Acme, "acme.example", "u-ghost", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        await _bed.Entities.DidNotReceiveWithAnyArgs().SetCatchAllAsync(default!, default!, default, Ct);
        await _bed.MailDomains.DidNotReceiveWithAnyArgs().SetCatchAllAsync(default!, default, Ct);
    }

    [Fact]
    public async Task Another_entitys_admin_or_a_malformed_domain_changes_nothing()
    {
        (await Set.ExecuteAsync(AdminTestBed.Acme, "not a domain", "u-alice", Ct)).Error!.Target.ShouldBe("domain");
        _bed.ActAsEntityAdmin(AdminTestBed.Other);
        (await Set.ExecuteAsync(AdminTestBed.Acme, "acme.example", "u-alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        await _bed.Entities.DidNotReceiveWithAnyArgs().SetCatchAllAsync(default!, default!, default, Ct);
        _bed.Audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_domain_the_entity_lacks_is_not_sent_to_the_mail_server()
    {
        _bed.Entities.SetCatchAllAsync(default!, default!, default, Ct).ReturnsForAnyArgs(Error.NotFound("The entity has no domain acme.org.", "domain"));

        (await Set.ExecuteAsync(AdminTestBed.Acme, "acme.org", "u-alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        await _bed.MailDomains.DidNotReceiveWithAnyArgs().SetCatchAllAsync(default!, default, Ct);
        _bed.Audit.Single("entity.domain.catch-all").Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task A_mail_server_failure_keeps_the_choice_and_says_to_save_again()
    {
        _bed.MailDomains.SetCatchAllAsync(default!, default, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("down"));

        Error error = (await Set.ExecuteAsync(AdminTestBed.Acme, "acme.example", "u-alice", Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        error.Message.ShouldContain("Save it again");
        await _bed.Entities.Received(1).SetCatchAllAsync(AdminTestBed.Acme, Acme, "u-alice", Arg.Any<CancellationToken>());
        _bed.Audit.Single("entity.domain.catch-all").Details["mailServer"].ShouldBe("failed");
    }

    [Fact]
    public async Task The_entitys_page_shows_the_mail_servers_catch_all()
    {
        _bed.MailDomains.FindAsync(Acme, Arg.Any<CancellationToken>()).Returns(new MailDomainInfo("d1", Acme, true) { CatchAllAddress = Alice });

        EntityDetail detail = (await new GetEntity(_bed.Actor, _bed.Entities, _bed.DomainProvisioning()).ExecuteAsync(AdminTestBed.Acme, Ct)).Value;

        detail.Domains.Single().CatchAllAddress.ShouldBe(Alice);
    }

    [Fact]
    public async Task Repair_sends_the_recorded_catch_all_again_when_the_mail_server_lost_it()
    {
        _bed.Entities.AddDomainAsync(default!, default!, Ct).ReturnsForAnyArgs(WithCatchAll("u-alice"));
        _bed.MailDomains.FindAsync(Acme, Arg.Any<CancellationToken>()).Returns(new MailDomainInfo("d1", Acme, true));

        DomainStatus status = (await new AddEntityDomain(_bed.Actor, _bed.Entities, _bed.DomainProvisioning(), _bed.Audit).ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        status.CatchAllAddress.ShouldBe(Alice);
        await _bed.MailDomains.Received(1).SetCatchAllAsync(Acme, AdminTestBed.Address(Alice), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Repair_leaves_a_catch_all_in_step_or_one_set_outside_the_console()
    {
        _bed.Entities.AddDomainAsync(default!, default!, Ct).ReturnsForAnyArgs(WithCatchAll("u-alice"));
        _bed.MailDomains.FindAsync(Acme, Arg.Any<CancellationToken>()).Returns(new MailDomainInfo("d1", Acme, true) { CatchAllAddress = "Alice@ACME.example" });
        var repair = new AddEntityDomain(_bed.Actor, _bed.Entities, _bed.DomainProvisioning(), _bed.Audit);

        await repair.ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct);
        _bed.Entities.AddDomainAsync(default!, default!, Ct).ReturnsForAnyArgs(AdminTestBed.AcmeEntity);
        _bed.MailDomains.FindAsync(Acme, Arg.Any<CancellationToken>()).Returns(new MailDomainInfo("d1", Acme, true) { CatchAllAddress = "postmaster@acme.example" });
        DomainStatus status = (await repair.ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        status.CatchAllAddress.ShouldBe("postmaster@acme.example");
        await _bed.MailDomains.DidNotReceiveWithAnyArgs().SetCatchAllAsync(default!, default, Ct);
    }

    [Fact]
    public async Task The_catch_all_users_mail_cannot_be_deleted_and_anyone_elses_preview_names_them()
    {
        _bed.Options.AcceptedAcrValues.Add("mfa");
        _bed.Entities.GetAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(WithCatchAll("u-carol"));
        _bed.AddUser("u-carol", "carol", "carol@acme.example", enabled: false);
        _bed.Mail.FindByAddressAsync("carol@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-carol", "carol@acme.example", true) { Enabled = false });
        _bed.AddCloudUser("carol", "carol@acme.example", enabled: false);
        var delete = new DeleteUserMail(_bed.Actor, _bed.DeletionGuard(), _bed.Mail, _bed.Identities, _bed.Time, _bed.Audit, _bed.History());

        Error refused = (await delete.ExecuteAsync("u-carol", "carol@acme.example", Ct)).Error!;

        refused.Kind.ShouldBe(ErrorKind.Conflict);
        refused.Message.ShouldContain("acme.example");
        await _bed.Mail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);
        _bed.Audit.Single("user.mail.deleted").Details["outcome"].ShouldBe("refused:catch-all");

        _bed.Entities.GetAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(WithCatchAll("u-alice"));
        DeletionPreview preview = (await new GetDeletionPreview(_bed.DeletionGuard(), _bed.Mail).ExecuteAsync("u-carol", DeletionTarget.Mail, Ct)).Value;
        preview.Removes.ShouldContain(r => r.Contains($"goes to {Alice}", StringComparison.Ordinal));
        (await delete.ExecuteAsync("u-carol", "carol@acme.example", Ct)).IsSuccess.ShouldBeTrue();
    }
}
