using Nightware.Application.Administration;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class DataDeletionTests
{
    private const string Address = "carol@acme.example";

    private readonly AdminTestBed _bed = new();

    public DataDeletionTests()
    {
        _bed.Options.AcceptedAcrValues.Add("mfa");
        _bed.AddUser("u-carol", "carol", Address, enabled: false);
        _bed.AddMailbox(Address, "m-carol");
        _bed.AddCloudUser("carol", Address, enabled: false);
    }

    private static CancellationToken Ct => AdminTestBed.Ct;

    private DeleteUserMail DeleteMail => new(_bed.Actor, _bed.DeletionGuard(), _bed.Mail, _bed.Identities, _bed.Time, _bed.Audit);

    private DeleteUserGroupwareData DeleteCloud => new(_bed.Actor, _bed.DeletionGuard(), _bed.Groupware, _bed.Identities, _bed.Time, _bed.Audit);

    [Fact]
    public async Task A_global_admin_deletes_mail_and_only_mail()
    {
        DeletionOutcome outcome = (await DeleteMail.ExecuteAsync("u-carol", " Carol@ACME.example ", Ct)).Value;

        outcome.ShouldBe(new DeletionOutcome(false, 1234));
        await _bed.Mail.Received(1).DestroyAsync("m-carol", Arg.Any<CancellationToken>());
        await _bed.Identities.Received(1).SetDeletionMarkerAsync("u-carol", DeletionTarget.Mail, _bed.Time.GetUtcNow(), Arg.Any<CancellationToken>());
        await _bed.Groupware.DidNotReceiveWithAnyArgs().DeleteAsync(default!, Ct);
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetEnabledAsync(default!, default, Ct);
        AuditEvent audit = _bed.Audit.Single("user.mail.deleted");
        audit.Details["outcome"].ShouldBe("deleted");
        audit.Details["bytes"].ShouldBe("1234");
        audit.Details["mailbox"].ShouldBe("m-carol");
    }

    [Fact]
    public async Task A_global_admin_deletes_nextcloud_data_and_only_that()
    {
        (await DeleteCloud.ExecuteAsync("u-carol", Address, Ct)).Value.AlreadyAbsent.ShouldBeFalse();

        await _bed.Groupware.Received(1).DeleteAsync("carol", Arg.Any<CancellationToken>());
        await _bed.Mail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);
        await _bed.Identities.Received(1).SetDeletionMarkerAsync("u-carol", DeletionTarget.GroupwareData, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_entity_admin_is_refused_unless_the_deployment_opts_in()
    {
        _bed.ActAsEntityAdmin();
        (await DeleteMail.ExecuteAsync("u-carol", Address, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _bed.Mail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);

        _bed.Options.EntityAdminsMayDeleteData = true;
        (await DeleteMail.ExecuteAsync("u-carol", Address, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task An_enabled_user_must_be_offboarded_first()
    {
        _bed.Update(new IdentityAccount("u-carol", "carol", Address, Enabled: true));

        (await DeleteMail.ExecuteAsync("u-carol", Address, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _bed.Mail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);
    }

    [Fact]
    public async Task Not_on_yourself()
    {
        _bed.ActAsGlobal("u-carol");

        (await DeleteMail.ExecuteAsync("u-carol", Address, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_stale_sign_in_must_re_authenticate()
    {
        _bed.SignedInAgo(TimeSpan.FromMinutes(6));

        (await DeleteMail.ExecuteAsync("u-carol", Address, Ct)).Error!.Code.ShouldBe("reauthenticationRequired");
        await _bed.Mail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);
    }

    [Fact]
    public async Task A_weak_sign_in_must_re_authenticate()
    {
        _bed.Options.AcceptedAcrValues.Clear();
        _bed.Options.AcceptedAcrValues.Add("phishing-resistant");

        (await DeleteCloud.ExecuteAsync("u-carol", Address, Ct)).Error!.Code.ShouldBe("reauthenticationRequired");
        await _bed.Groupware.DidNotReceiveWithAnyArgs().DeleteAsync(default!, Ct);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("carol@acme.exampl")]
    [InlineData("bob@acme.example")]
    public async Task The_address_must_be_retyped_exactly(string? typed)
    {
        (await DeleteMail.ExecuteAsync("u-carol", typed, Ct)).Error!.Target.ShouldBe("confirmation");
        await _bed.Mail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);
    }

    [Fact]
    public async Task A_mailbox_that_is_not_provably_this_users_is_never_deleted()
    {
        _bed.AddMailbox(Address, "m-group", individual: false);

        (await DeleteMail.ExecuteAsync("u-carol", Address, Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
        await _bed.Mail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);
        _bed.Audit.Single("user.mail.deleted").Details["outcome"].ShouldBe("refused:Conflict");
    }

    [Fact]
    public async Task A_nextcloud_user_with_another_address_is_never_deleted()
    {
        _bed.AddCloudUser("carol", "someone@else.example", enabled: false);

        (await DeleteCloud.ExecuteAsync("u-carol", Address, Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
        await _bed.Groupware.DidNotReceiveWithAnyArgs().DeleteAsync(default!, Ct);
    }

    [Fact]
    public async Task Deleting_twice_reports_already_absent()
    {
        _bed.Mail.FindByAddressAsync(Address, Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));
        _bed.Groupware.FindAsync("carol", Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));

        (await DeleteMail.ExecuteAsync("u-carol", Address, Ct)).Value.AlreadyAbsent.ShouldBeTrue();
        (await DeleteCloud.ExecuteAsync("u-carol", Address, Ct)).Value.AlreadyAbsent.ShouldBeTrue();
        await _bed.Mail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);
        _bed.Audit.Single("user.mail.deleted").Details["outcome"].ShouldBe("already absent");
    }

    [Fact]
    public async Task A_race_where_the_account_vanished_is_already_absent()
    {
        _bed.Mail.DestroyAsync(default!, Ct).ReturnsForAnyArgs(Error.NotFound("gone"));

        (await DeleteMail.ExecuteAsync("u-carol", Address, Ct)).Value.ShouldBe(new DeletionOutcome(true, null));
    }

    [Fact]
    public async Task A_backend_failure_writes_no_marker()
    {
        _bed.Mail.DestroyAsync(default!, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("down"));

        (await DeleteMail.ExecuteAsync("u-carol", Address, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetDeletionMarkerAsync(default!, default, default, Ct);
        _bed.Audit.Single("user.mail.deleted").Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task The_preview_says_what_is_removed_and_kept()
    {
        var preview = new GetDeletionPreview(_bed.DeletionGuard(), _bed.Mail);

        DeletionPreview mail = (await preview.ExecuteAsync("u-carol", DeletionTarget.Mail, Ct)).Value;
        mail.MailboxBytes.ShouldBe(1234);
        mail.AlreadyAbsent.ShouldBeFalse();
        mail.Removes[0].ShouldContain(Address);
        mail.Keeps.ShouldContain(k => k.Contains("Nextcloud", StringComparison.Ordinal));

        DeletionPreview cloud = (await preview.ExecuteAsync("u-carol", DeletionTarget.GroupwareData, Ct)).Value;
        cloud.Keeps.ShouldContain(k => k.Contains(Address, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_preview_applies_the_same_permission_rules()
    {
        _bed.ActAsEntityAdmin();

        (await new GetDeletionPreview(_bed.DeletionGuard(), _bed.Mail).ExecuteAsync("u-carol", DeletionTarget.Mail, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }
}
