using Nightware.Application.Administration;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class ProvisioningTests
{
    private readonly AdminTestBed _bed = new();

    private static CancellationToken Ct => AdminTestBed.Ct;

    private static NewUserRequest Carol(string? username = null, params string[] aliases) =>
        new(AdminTestBed.Acme, "Carol@acme.example", username, "Carol", "Jones", aliases);

    private PlanUserProvisioning Plan => new(_bed.Planner());

    private ApplyUserProvisioning Apply => new(_bed.Actor, _bed.Planner(), _bed.Entities, _bed.Identities, _bed.Mail, _bed.Groupware, _bed.Credentials(), _bed.Audit);

    [Fact]
    public async Task A_new_person_is_created_everywhere()
    {
        ProvisioningPlan plan = (await Plan.ExecuteAsync(Carol(), Ct)).Value;

        plan.Steps.Select(s => s.Action).ShouldBe([PlannedAction.Create, PlannedAction.Create, PlannedAction.Create]);
        plan.For(BackendKind.Identity).Reason.ShouldContain("carol");
    }

    [Fact]
    public async Task Existing_accounts_are_linked_not_recreated()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example", entities: []);
        _bed.AddMailbox("carol@acme.example");
        _bed.AddCloudUser("carol", "carol@acme.example");

        ProvisioningPlan plan = (await Plan.ExecuteAsync(Carol(), Ct)).Value;
        plan.Steps.Select(s => s.Action).ShouldBe([PlannedAction.Link, PlannedAction.Link, PlannedAction.Link]);

        ProvisioningResult result = (await Apply.ExecuteAsync(Carol(), CredentialDelivery.None, Ct)).Value;

        result.Links.Select(l => l.State).ShouldAllBe(s => s == LinkState.Linked);
        await _bed.Identities.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
        await _bed.Mail.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
        await _bed.Groupware.DidNotReceiveWithAnyArgs().PreProvisionAsync(default!, default!, default!, Ct);
        await _bed.Entities.Received(1).AddMemberAsync(AdminTestBed.Acme, "u-carol", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_mailbox_that_exists_without_a_keycloak_user_is_linked_to_the_new_user()
    {
        _bed.AddMailbox("carol@acme.example");

        ProvisioningResult result = (await Apply.ExecuteAsync(Carol(), CredentialDelivery.None, Ct)).Value;

        await _bed.Identities.Received(1).CreateAsync(Arg.Is<NewIdentity>(n => n.Username == "carol" && n.Email.Value == "carol@acme.example" && n.FirstName == "Carol"), Arg.Any<CancellationToken>());
        await _bed.Mail.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
        await _bed.Groupware.Received(1).PreProvisionAsync("carol", "carol@acme.example", "Carol Jones", Arg.Any<CancellationToken>());
        result.Links.Single(l => l.Backend == BackendKind.Mail).DownstreamId.ShouldBe("m-1");
    }

    [Fact]
    public async Task Apply_creates_in_order_and_audits()
    {
        ProvisioningResult result = (await Apply.ExecuteAsync(Carol(null, "c.jones@acme.example"), CredentialDelivery.ActionEmail, Ct)).Value;

        result.User.Id.ShouldBe("u-new");
        result.Credential.ShouldBeNull();
        result.Links.Select(l => l.State).ShouldAllBe(s => s == LinkState.Linked);
        Received.InOrder(() =>
        {
            _bed.Identities.CreateAsync(Arg.Any<NewIdentity>(), Arg.Any<CancellationToken>());
            _bed.Entities.AddMemberAsync(AdminTestBed.Acme, "u-new", Arg.Any<CancellationToken>());
            _bed.Mail.CreateAsync(Arg.Any<MailAddress>(), "carol", Arg.Any<CancellationToken>());
            _bed.Mail.SetAliasesAsync("m-new", Arg.Is<IReadOnlyList<MailAddress>>(a => a.Single().Value == "c.jones@acme.example"), Arg.Any<CancellationToken>());
            _bed.Groupware.PreProvisionAsync("carol", "carol@acme.example", Arg.Any<string>(), Arg.Any<CancellationToken>());
            _bed.Identities.SendActionEmailAsync("u-new", Arg.Is<IReadOnlyList<RequiredAction>>(a => a.Single() == RequiredAction.UpdatePassword), _bed.Options.ActionEmailLifespan, Arg.Any<CancellationToken>());
        });
        AuditEvent audit = _bed.Audit.Single("user.provisioned");
        audit.Succeeded.ShouldBeTrue();
        audit.TargetUserId.ShouldBe("u-new");
        audit.Details["identityAction"].ShouldBe("Create");
        audit.Details["mail"].ShouldBe("Linked");
    }

    [Fact]
    public async Task A_temporary_password_is_returned_once_and_never_audited()
    {
        ProvisioningResult result = (await Apply.ExecuteAsync(Carol(), CredentialDelivery.TemporaryPassword, Ct)).Value;

        result.Credential!.Password.ShouldBe("Generated-Pass-1");
        await _bed.Identities.Received(1).SetTemporaryPasswordAsync("u-new", result.Credential, Arg.Any<CancellationToken>());
        _bed.Audit.Events.SelectMany(e => e.Details.Values).ShouldNotContain("Generated-Pass-1");
    }

    [Fact]
    public async Task A_conflict_blocks_every_write()
    {
        _bed.AddCloudUser("carol", "someone-else@acme.example");

        Result<ProvisioningResult> result = await Apply.ExecuteAsync(Carol(), CredentialDelivery.TemporaryPassword, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        result.Error.Target.ShouldBe("Groupware");
        await _bed.Identities.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
        await _bed.Mail.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetTemporaryPasswordAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task An_alias_held_by_another_mailbox_is_a_conflict()
    {
        _bed.AddMailbox("sales@acme.example", "m-sales");

        ProvisioningPlan plan = (await Plan.ExecuteAsync(Carol(null, "sales@acme.example"), Ct)).Value;

        plan.For(BackendKind.Mail).Action.ShouldBe(PlannedAction.Conflict);
        plan.For(BackendKind.Mail).Reason.ShouldContain("sales@acme.example");
    }

    [Fact]
    public async Task A_later_failure_is_reported_without_rolling_back()
    {
        _bed.Mail.CreateAsync(default!, default!, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("Stalwart is down."));

        ProvisioningResult result = (await Apply.ExecuteAsync(Carol(), CredentialDelivery.None, Ct)).Value;

        result.Links.Single(l => l.Backend == BackendKind.Identity).State.ShouldBe(LinkState.Linked);
        result.Links.Single(l => l.Backend == BackendKind.Mail).State.ShouldBe(LinkState.Error);
        result.Links.Single(l => l.Backend == BackendKind.Groupware).State.ShouldBe(LinkState.Linked);
        _bed.Audit.Single("user.provisioned").Succeeded.ShouldBeFalse();
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetEnabledAsync(default!, default, Ct);
    }

    [Fact]
    public async Task Without_pre_provisioning_nextcloud_is_pending()
    {
        _bed.Groupware.PreProvisionAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(Error.Unsupported("no API"));

        ProvisioningResult result = (await Apply.ExecuteAsync(Carol(), CredentialDelivery.None, Ct)).Value;

        result.Links.Single(l => l.Backend == BackendKind.Groupware).State.ShouldBe(LinkState.Pending);
        _bed.Audit.Single("user.provisioned").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task A_disabled_nextcloud_account_is_re_enabled()
    {
        _bed.AddCloudUser("carol", "carol@acme.example", enabled: false);

        ProvisioningResult result = (await Apply.ExecuteAsync(Carol(), CredentialDelivery.None, Ct)).Value;

        await _bed.Groupware.Received(1).SetEnabledAsync("carol", true, Arg.Any<CancellationToken>());
        result.Links.Single(l => l.Backend == BackendKind.Groupware).State.ShouldBe(LinkState.Linked);
    }

    [Fact]
    public async Task A_credential_failure_is_reported_alongside_the_accounts()
    {
        _bed.Identities.SendActionEmailAsync(default!, default!, default, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("SMTP is not configured."));

        ProvisioningResult result = (await Apply.ExecuteAsync(Carol(), CredentialDelivery.ActionEmail, Ct)).Value;

        result.CredentialError.ShouldBe("SMTP is not configured.");
        result.Links.Select(l => l.State).ShouldAllBe(s => s == LinkState.Linked);
    }

    [Theory]
    [InlineData("carol@other.example", "address")]
    [InlineData("not an address", "address")]
    public async Task Addresses_off_the_entity_or_malformed_are_refused(string address, string target)
    {
        Result<ProvisioningPlan> plan = await Plan.ExecuteAsync(Carol() with { Address = address }, Ct);

        plan.Error!.Kind.ShouldBe(ErrorKind.Validation);
        plan.Error.Target.ShouldBe(target);
    }

    [Fact]
    public async Task Aliases_off_the_entity_are_refused() =>
        (await Plan.ExecuteAsync(Carol(null, "carol@other.example"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);

    [Theory]
    [InlineData("a")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public async Task Malformed_usernames_are_refused(string username) =>
        (await Plan.ExecuteAsync(Carol(username), Ct)).Error!.Target.ShouldBe("username");

    [Fact]
    public async Task An_entity_admin_cannot_provision_into_another_entity()
    {
        _bed.ActAsEntityAdmin(AdminTestBed.Other);

        (await Apply.ExecuteAsync(Carol(), CredentialDelivery.TemporaryPassword, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _bed.Identities.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
    }

    [Fact]
    public async Task An_entity_admin_cannot_link_a_global_admin_and_reset_their_password()
    {
        _bed.ActAsEntityAdmin();
        _bed.AddUser("u-root", "carol", "carol@acme.example", entities: [], scope: new AdminScope(true, new HashSet<string>()));

        Result<ProvisioningResult> result = await Apply.ExecuteAsync(Carol(), CredentialDelivery.TemporaryPassword, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        result.Error.Message.ShouldContain("outside your scope");
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetTemporaryPasswordAsync(default!, default!, Ct);
        await _bed.Entities.DidNotReceiveWithAnyArgs().AddMemberAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task An_entity_admin_may_link_a_plain_user_without_an_entity()
    {
        _bed.ActAsEntityAdmin();
        _bed.AddUser("u-carol", "carol", "carol@acme.example", entities: []);

        (await Plan.ExecuteAsync(Carol(), Ct)).Value.For(BackendKind.Identity).Action.ShouldBe(PlannedAction.Link);
    }

    [Fact]
    public async Task A_directory_outage_fails_the_plan_instead_of_planning_a_duplicate()
    {
        _bed.Identities.FindByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Keycloak down"));

        (await Plan.ExecuteAsync(Carol(), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task A_mail_server_outage_blocks_the_plan()
    {
        _bed.Mail.FindByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        (await Plan.ExecuteAsync(Carol(), Ct)).Value.For(BackendKind.Mail).Action.ShouldBe(PlannedAction.Conflict);
    }

    [Fact]
    public async Task A_failed_identity_create_stops_and_audits_failure()
    {
        _bed.Identities.CreateAsync(default!, Ct).ReturnsForAnyArgs(Error.Conflict("exists"));

        (await Apply.ExecuteAsync(Carol(), CredentialDelivery.None, Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
        await _bed.Mail.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
        _bed.Audit.Single("user.provisioned").Succeeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData("nightware-admin@acme.example")]
    [InlineData("postmaster@acme.example")]
    [InlineData("NoReply@acme.example")]
    public async Task Reserved_system_addresses_are_never_given_to_a_person(string address)
    {
        Result<ProvisioningResult> result = await Apply.ExecuteAsync(Carol() with { Address = address }, CredentialDelivery.TemporaryPassword, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Message.ShouldContain("reserved");
        await _bed.Identities.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
    }

    [Fact]
    public async Task A_privileged_mailbox_cannot_be_claimed_by_a_new_user()
    {
        _bed.Mail.FindByAddressAsync("ops@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-ops", "ops@acme.example", true) { IsPrivileged = true });

        Result<ProvisioningResult> result = await Apply.ExecuteAsync(Carol() with { Address = "ops@acme.example" }, CredentialDelivery.TemporaryPassword, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        result.Error.Message.ShouldContain("privileged");
        await _bed.Identities.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
    }

    [Fact]
    public async Task A_nextcloud_administrator_cannot_be_linked()
    {
        _bed.Groupware.FindAsync("carol", Arg.Any<CancellationToken>()).Returns(new GroupwareAccount("carol", "carol@acme.example", "C", true, true) { IsAdministrator = true });

        (await Plan.ExecuteAsync(Carol(), Ct)).Value.For(BackendKind.Groupware).Action.ShouldBe(PlannedAction.Conflict);
    }

    [Fact]
    public async Task An_alias_that_is_someone_elses_sign_in_address_is_a_conflict()
    {
        _bed.AddUser("u-victim", "victim", "boss@acme.example");

        ProvisioningPlan plan = (await Plan.ExecuteAsync(Carol(null, "boss@acme.example"), Ct)).Value;

        plan.For(BackendKind.Mail).Action.ShouldBe(PlannedAction.Conflict);
        plan.For(BackendKind.Mail).Reason.ShouldContain("Keycloak user victim");
    }

    [Fact]
    public async Task An_offboarded_user_is_reinstated_not_relinked()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example", enabled: false, entities: []);

        ProvisioningPlan plan = (await Plan.ExecuteAsync(Carol(), Ct)).Value;

        plan.For(BackendKind.Identity).Action.ShouldBe(PlannedAction.Conflict);
        plan.For(BackendKind.Identity).Reason.ShouldContain("reinstate");
    }

    [Fact]
    public async Task Linking_an_existing_person_never_resets_their_password()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example", entities: []);

        (await Apply.ExecuteAsync(Carol(), CredentialDelivery.TemporaryPassword, Ct)).Error!.Target.ShouldBe("delivery");
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetTemporaryPasswordAsync(default!, default!, Ct);
        (await Apply.ExecuteAsync(Carol(), CredentialDelivery.None, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Nobody_links_their_own_account()
    {
        _bed.AddUser("u-admin", "carol", "carol@acme.example", entities: []);

        (await Apply.ExecuteAsync(Carol(), CredentialDelivery.None, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Even_a_global_admin_cannot_link_a_keycloak_administrator()
    {
        _bed.AddUser("u-kcadmin", "carol", "carol@acme.example", entities: [], scope: new AdminScope(false, new HashSet<string>(), AdministersDirectory: true));

        ProvisioningPlan plan = (await Plan.ExecuteAsync(Carol(), Ct)).Value;

        plan.For(BackendKind.Identity).Action.ShouldBe(PlannedAction.Conflict);
        plan.For(BackendKind.Identity).Reason.ShouldContain("administers Keycloak");
    }
}
