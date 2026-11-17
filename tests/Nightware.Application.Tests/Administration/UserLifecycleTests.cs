using Nightware.Application.Administration;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class UserLifecycleTests
{
    private readonly AdminTestBed _bed = new();

    private static CancellationToken Ct => AdminTestBed.Ct;

    private IdentityAccount Carol(bool enabled = true, string[]? entities = null, AdminScope? scope = null)
    {
        IdentityAccount carol = _bed.AddUser("u-carol", "carol", "carol@acme.example", enabled, entities, scope);
        _bed.AddMailbox("carol@acme.example", "m-carol");
        _bed.AddCloudUser("carol", "carol@acme.example", enabled);
        return carol;
    }

    [Fact]
    public async Task GetUser_shows_every_backend()
    {
        Carol();

        UserDetail detail = (await new GetUser(_bed.UserAccess()).ExecuteAsync("u-carol", Ct)).Value;

        detail.User.EntityId.ShouldBe(AdminTestBed.Acme);
        detail.Links.Select(l => (l.Backend, l.State)).ShouldBe(
            [(BackendKind.Identity, LinkState.Linked), (BackendKind.Mail, LinkState.Linked), (BackendKind.Groupware, LinkState.Linked)]);
    }

    [Fact]
    public async Task An_entity_admin_cannot_see_users_outside_their_scope_and_learns_nothing()
    {
        Carol(entities: [AdminTestBed.Other]);
        _bed.ActAsEntityAdmin();

        (await new GetUser(_bed.UserAccess()).ExecuteAsync("u-carol", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_global_admin_is_told_why_a_user_is_out_of_scope_only_when_global()
    {
        Carol(entities: [AdminTestBed.Other]);

        (await new GetUser(_bed.UserAccess()).ExecuteAsync("u-carol", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_non_admin_is_refused()
    {
        Carol();
        _bed.ActAsNobody();

        (await new GetUser(_bed.UserAccess()).ExecuteAsync("u-carol", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task ListUsers_shows_members_with_links_in_address_order()
    {
        _bed.Entities.ListMembersAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success(
        [
            new IdentityAccount("u2", "zed", "zed@acme.example", true),
            new IdentityAccount("u1", "amy", "amy@acme.example", true),
        ]));
        _bed.AddMailbox("amy@acme.example");

        IReadOnlyList<UserDetail> users = (await new ListUsers(_bed.Actor, _bed.Entities, _bed.UserAccess()).ExecuteAsync(AdminTestBed.Acme, Ct)).Value;

        users.Select(u => u.User.Identity.Username).ShouldBe(["amy", "zed"]);
        users[0].For(BackendKind.Mail).State.ShouldBe(LinkState.Linked);
        users[1].For(BackendKind.Mail).State.ShouldBe(LinkState.Missing);
    }

    [Fact]
    public async Task ListUsers_is_scoped_to_the_actors_entities()
    {
        _bed.ActAsEntityAdmin();

        (await new ListUsers(_bed.Actor, _bed.Entities, _bed.UserAccess()).ExecuteAsync(AdminTestBed.Other, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _bed.Entities.DidNotReceiveWithAnyArgs().ListMembersAsync(default!, Ct);
    }

    [Fact]
    public async Task Offboarding_disables_ends_sessions_and_deletes_nothing()
    {
        Carol();
        var offboard = new OffboardUser(_bed.Actor, _bed.UserAccess(), _bed.Identities, _bed.Mail, _bed.Groupware, _bed.Audit);

        (await offboard.ExecuteAsync("u-carol", Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Identities.Received(1).SetEnabledAsync("u-carol", false, Arg.Any<CancellationToken>());
        await _bed.Identities.Received(1).EndSessionsAsync("u-carol", Arg.Any<CancellationToken>());
        await _bed.Groupware.Received(1).SetEnabledAsync("carol", false, Arg.Any<CancellationToken>());
        // App passwords and API keys made in Stalwart would outlive the Keycloak account otherwise.
        await _bed.Mail.Received(1).SetEnabledAsync("m-carol", false, Arg.Any<CancellationToken>());
        await _bed.Mail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);
        await _bed.Groupware.DidNotReceiveWithAnyArgs().DeleteAsync(default!, Ct);
        _bed.Audit.Single("user.offboarded").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Offboarding_fails_when_the_mail_server_cannot_be_checked_and_still_shuts_the_rest()
    {
        Carol();
        _bed.Mail.FindByAddressAsync("carol@acme.example", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Stalwart is down."));

        Error error = (await Offboard().ExecuteAsync("u-carol", Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        error.Message.ShouldContain("Mail could not be checked");
        await _bed.Identities.Received(1).SetEnabledAsync("u-carol", false, Arg.Any<CancellationToken>());
        await _bed.Groupware.Received(1).SetEnabledAsync("carol", false, Arg.Any<CancellationToken>());
        _bed.Audit.Single("user.offboarded").Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Offboarding_keeps_shutting_mail_and_nextcloud_when_keycloak_fails()
    {
        Carol();
        _bed.Identities.SetEnabledAsync("u-carol", false, Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Keycloak timed out."));

        (await Offboard().ExecuteAsync("u-carol", Ct)).Error!.Message.ShouldContain("Keycloak: Keycloak timed out.");

        await _bed.Mail.Received(1).SetEnabledAsync("m-carol", false, Arg.Any<CancellationToken>());
        await _bed.Groupware.Received(1).SetEnabledAsync("carol", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Offboarding_a_disabled_user_again_finishes_what_was_left_open()
    {
        Carol(enabled: false);
        UserDetail before = (await new GetUser(_bed.UserAccess()).ExecuteAsync("u-carol", Ct)).Value;
        OffboardUser.IsIncomplete(before).ShouldBeTrue();
        OffboardUser.StillOpen(before).Select(l => l.Backend).ShouldBe([BackendKind.Mail]);

        (await Offboard().ExecuteAsync("u-carol", Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Mail.Received(1).SetEnabledAsync("m-carol", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_offboarding_is_complete_when_nothing_downstream_can_still_be_signed_in_to()
    {
        Carol(enabled: false);
        _bed.Mail.FindByAddressAsync("carol@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-carol", "carol@acme.example", true) { Enabled = false });

        OffboardUser.IsIncomplete((await new GetUser(_bed.UserAccess()).ExecuteAsync("u-carol", Ct)).Value).ShouldBeFalse();
    }

    private OffboardUser Offboard() => new(_bed.Actor, _bed.UserAccess(), _bed.Identities, _bed.Mail, _bed.Groupware, _bed.Audit);

    [Fact]
    public async Task Nobody_offboards_themselves()
    {
        Carol();
        _bed.ActAsGlobal("u-carol");

        (await new OffboardUser(_bed.Actor, _bed.UserAccess(), _bed.Identities, _bed.Mail, _bed.Groupware, _bed.Audit).ExecuteAsync("u-carol", Ct))
            .Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetEnabledAsync(default!, default, Ct);
    }

    [Fact]
    public async Task An_entity_admin_cannot_offboard_a_global_admin_in_their_entity()
    {
        Carol(scope: new AdminScope(true, new HashSet<string>()));
        _bed.ActAsEntityAdmin();

        (await new OffboardUser(_bed.Actor, _bed.UserAccess(), _bed.Identities, _bed.Mail, _bed.Groupware, _bed.Audit).ExecuteAsync("u-carol", Ct))
            .Error!.Kind.ShouldBe(ErrorKind.NotFound);
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetEnabledAsync(default!, default, Ct);
    }

    [Fact]
    public async Task Reinstate_enables_and_recreates_a_deleted_mailbox_only_when_asked()
    {
        IdentityAccount carol = Carol(enabled: false) with { MailDeletedAt = _bed.Time.GetUtcNow() };
        _bed.Update(carol);
        _bed.Mail.FindByAddressAsync("carol@acme.example", Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));
        var reinstate = new ReinstateUser(_bed.Actor, _bed.UserAccess(), _bed.Identities, _bed.Mail, _bed.Groupware, _bed.Audit);

        (await reinstate.ExecuteAsync("u-carol", recreateMailbox: false, Ct)).IsSuccess.ShouldBeTrue();
        await _bed.Mail.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
        await _bed.Identities.Received(1).SetEnabledAsync("u-carol", true, Arg.Any<CancellationToken>());
        await _bed.Groupware.Received(1).SetEnabledAsync("carol", true, Arg.Any<CancellationToken>());

        (await reinstate.ExecuteAsync("u-carol", recreateMailbox: true, Ct)).IsSuccess.ShouldBeTrue();
        await _bed.Mail.Received(1).CreateAsync(Arg.Is<MailAddress>(a => a.Value == "carol@acme.example"), "carol", Arg.Any<CancellationToken>());
        await _bed.Identities.Received(1).SetDeletionMarkerAsync("u-carol", DeletionTarget.Mail, null, Arg.Any<CancellationToken>());
        _bed.Audit.Events.Last().Details["mailboxRecreated"].ShouldBe("true");
    }

    [Fact]
    public async Task Reinstate_recreates_deleted_nextcloud_data_as_an_empty_user()
    {
        IdentityAccount carol = Carol(enabled: false) with { GroupwareDeletedAt = _bed.Time.GetUtcNow() };
        _bed.Update(carol);
        _bed.Groupware.FindAsync("carol", Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));

        (await new ReinstateUser(_bed.Actor, _bed.UserAccess(), _bed.Identities, _bed.Mail, _bed.Groupware, _bed.Audit).ExecuteAsync("u-carol", false, Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Groupware.Received(1).PreProvisionAsync("carol", "carol@acme.example", "carol", Arg.Any<CancellationToken>());
        await _bed.Identities.Received(1).SetDeletionMarkerAsync("u-carol", DeletionTarget.GroupwareData, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Repair_creates_only_what_is_missing()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example");
        var repair = new RepairUser(_bed.Actor, _bed.UserAccess(), _bed.Mail, _bed.Groupware, _bed.Audit);

        (await repair.ExecuteAsync("u-carol", Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Mail.Received(1).CreateAsync(Arg.Is<MailAddress>(a => a.Value == "carol@acme.example"), "carol", Arg.Any<CancellationToken>());
        await _bed.Groupware.Received(1).PreProvisionAsync("carol", "carol@acme.example", "carol", Arg.Any<CancellationToken>());
        _bed.Audit.Single("user.repaired").Details["changes"].ShouldBe("mail:created,groupware:created");
    }

    [Fact]
    public async Task Repair_is_a_no_op_when_everything_is_linked()
    {
        Carol();

        (await new RepairUser(_bed.Actor, _bed.UserAccess(), _bed.Mail, _bed.Groupware, _bed.Audit).ExecuteAsync("u-carol", Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Mail.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
        _bed.Audit.Single("user.repaired").Details["changes"].ShouldBe("none");
    }

    [Fact]
    public async Task Repair_never_undoes_an_offboarding_or_recreates_deleted_mail()
    {
        Carol(enabled: false);
        var repair = new RepairUser(_bed.Actor, _bed.UserAccess(), _bed.Mail, _bed.Groupware, _bed.Audit);
        (await repair.ExecuteAsync("u-carol", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);

        IdentityAccount carol = _bed.AddUser("u-carol", "carol", "carol@acme.example") with { MailDeletedAt = _bed.Time.GetUtcNow() };
        _bed.Update(carol);
        _bed.Mail.FindByAddressAsync("carol@acme.example", Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));
        (await repair.ExecuteAsync("u-carol", Ct)).Value.For(BackendKind.Mail).Detail.ShouldStartWith("Mail deleted on 2026-11-17");
        await _bed.Mail.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Repair_re_enables_a_disabled_nextcloud_account()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example");
        _bed.AddMailbox("carol@acme.example");
        _bed.AddCloudUser("carol", "carol@acme.example", enabled: false);

        await new RepairUser(_bed.Actor, _bed.UserAccess(), _bed.Mail, _bed.Groupware, _bed.Audit).ExecuteAsync("u-carol", Ct);

        await _bed.Groupware.Received(1).SetEnabledAsync("carol", true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CredentialDelivery.ActionEmail)]
    [InlineData(CredentialDelivery.TemporaryPassword)]
    public async Task Credentials_are_reset_without_an_admin_choosing_a_password(CredentialDelivery delivery)
    {
        Carol();
        var reset = new ResetUserCredential(_bed.Actor, _bed.UserAccess(), _bed.Credentials(), _bed.Audit);

        TemporaryCredential? credential = (await reset.ExecuteAsync("u-carol", delivery, Ct)).Value;

        (credential is not null).ShouldBe(delivery == CredentialDelivery.TemporaryPassword);
        _bed.Audit.Single("user.credential.reset").Details["delivery"].ShouldBe(delivery.ToString());
    }

    [Fact]
    public async Task Credentials_are_not_reset_for_self_none_or_disabled_users()
    {
        Carol(enabled: false);
        var reset = new ResetUserCredential(_bed.Actor, _bed.UserAccess(), _bed.Credentials(), _bed.Audit);

        (await reset.ExecuteAsync("u-carol", CredentialDelivery.None, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await reset.ExecuteAsync("u-carol", CredentialDelivery.TemporaryPassword, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _bed.ActAsGlobal("u-carol");
        (await reset.ExecuteAsync("u-carol", CredentialDelivery.TemporaryPassword, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetTemporaryPasswordAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Aliases_are_written_to_keycloak_and_the_mailbox()
    {
        Carol();
        var setAliases = new SetUserAliases(_bed.Actor, _bed.UserAccess(), _bed.Entities, _bed.Identities, _bed.Mail, _bed.Options, _bed.Audit);

        (await setAliases.ExecuteAsync("u-carol", ["c.jones@acme.example", "carol@acme.example", " "], Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Identities.Received(1).SetAliasesAsync("u-carol", Arg.Is<IReadOnlyList<MailAddress>>(a => a.Single().Value == "c.jones@acme.example"), Arg.Any<CancellationToken>());
        await _bed.Mail.Received(1).SetAliasesAsync("m-carol", Arg.Is<IReadOnlyList<MailAddress>>(a => a.Single().Value == "c.jones@acme.example"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Aliases_off_the_entity_or_held_by_another_mailbox_are_refused()
    {
        Carol();
        _bed.AddMailbox("sales@acme.example", "m-sales");
        var setAliases = new SetUserAliases(_bed.Actor, _bed.UserAccess(), _bed.Entities, _bed.Identities, _bed.Mail, _bed.Options, _bed.Audit);

        (await setAliases.ExecuteAsync("u-carol", ["carol@other.example"], Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await setAliases.ExecuteAsync("u-carol", ["sales@acme.example"], Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetAliasesAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_keycloak_administrator_cannot_be_offboarded_or_reset_through_the_console()
    {
        Carol(scope: new AdminScope(false, new HashSet<string>(), AdministersDirectory: true));

        (await new OffboardUser(_bed.Actor, _bed.UserAccess(), _bed.Identities, _bed.Mail, _bed.Groupware, _bed.Audit).ExecuteAsync("u-carol", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new ResetUserCredential(_bed.Actor, _bed.UserAccess(), _bed.Credentials(), _bed.Audit).ExecuteAsync("u-carol", CredentialDelivery.TemporaryPassword, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetTemporaryPasswordAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Nobody_reinstates_themselves()
    {
        Carol(enabled: false);
        _bed.ActAsGlobal("u-carol");

        (await new ReinstateUser(_bed.Actor, _bed.UserAccess(), _bed.Identities, _bed.Mail, _bed.Groupware, _bed.Audit).ExecuteAsync("u-carol", false, Ct))
            .Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetEnabledAsync(default!, default, Ct);
    }

    [Fact]
    public async Task A_nextcloud_or_mail_administrator_cannot_have_their_password_reset()
    {
        Carol();
        _bed.AddCloudUser("carol", "carol@acme.example", enabled: true) ;
        _bed.Groupware.FindAsync("carol", Arg.Any<CancellationToken>()).Returns(new GroupwareAccount("carol", "carol@acme.example", "C", true, true) { IsAdministrator = true });
        var reset = new ResetUserCredential(_bed.Actor, _bed.UserAccess(), _bed.Credentials(), _bed.Audit);

        (await reset.ExecuteAsync("u-carol", CredentialDelivery.TemporaryPassword, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _bed.ActAsEntityAdmin();
        (await reset.ExecuteAsync("u-carol", CredentialDelivery.TemporaryPassword, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetTemporaryPasswordAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Aliases_cannot_capture_another_persons_address_or_a_reserved_one()
    {
        Carol();
        _bed.AddUser("u-boss", "boss", "boss@acme.example");
        var setAliases = new SetUserAliases(_bed.Actor, _bed.UserAccess(), _bed.Entities, _bed.Identities, _bed.Mail, _bed.Options, _bed.Audit);

        (await setAliases.ExecuteAsync("u-carol", ["boss@acme.example"], Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
        (await setAliases.ExecuteAsync("u-carol", ["postmaster@acme.example"], Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetAliasesAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Reinstating_restores_mail_sign_in()
    {
        Carol(enabled: false);
        _bed.Mail.FindByAddressAsync("carol@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-carol", "carol@acme.example", true) { Enabled = false });

        (await new ReinstateUser(_bed.Actor, _bed.UserAccess(), _bed.Identities, _bed.Mail, _bed.Groupware, _bed.Audit).ExecuteAsync("u-carol", false, Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Mail.Received(1).SetEnabledAsync("m-carol", true, Arg.Any<CancellationToken>());
    }
}
