using Nightware.Application.Administration;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class AccessHistoryRecorderTests
{
    private readonly AdminTestBed _bed = new();

    private static CancellationToken Ct => AdminTestBed.Ct;

    private static UserDetail Carol(params BackendLink[] links) =>
        new(new ManagedUser(new IdentityAccount("u-carol", "carol", "carol@acme.example", false), AdminTestBed.Acme, false, false),
            [new BackendLink(BackendKind.Identity, LinkState.Disabled), .. links]);

    [Fact]
    public async Task Names_the_admin_by_username_and_keeps_every_systems_state_sign_in_first()
    {
        _bed.AddUser("u-admin", "ops", "ops@example.net");
        AccessEvent? written = null;
        _bed.Identities.AddAccessEventAsync("u-carol", Arg.Do<AccessEvent>(e => written = e), Arg.Any<CancellationToken>()).Returns(Result.Success());

        await _bed.History().RecordAsync("u-carol", AccessEventKind.Offboarded, Carol(new BackendLink(BackendKind.Mail, LinkState.Disabled)), Ct);

        written!.By.ShouldBe("ops");
        written.Systems.ShouldBe([new SystemState(BackendKind.Identity, LinkState.Disabled), new SystemState(BackendKind.Mail, LinkState.Disabled)]);
    }

    [Fact]
    public async Task Falls_back_to_the_admins_id_when_their_account_cannot_be_read()
    {
        AccessEvent? written = null;
        _bed.Identities.AddAccessEventAsync("u-carol", Arg.Do<AccessEvent>(e => written = e), Arg.Any<CancellationToken>()).Returns(Result.Success());

        await _bed.History().RecordAsync("u-carol", AccessEventKind.Reinstated, after: null, Ct);

        written!.By.ShouldBe("u-admin");
        written.Systems.ShouldBeEmpty();
    }

    [Fact]
    public void A_deleted_account_reads_as_missing_and_nothing_else_changes()
    {
        UserDetail after = AccessHistoryRecorder.WithGone(Carol(new BackendLink(BackendKind.Mail, LinkState.Disabled, "shut", "m-1"), new BackendLink(BackendKind.Groupware, LinkState.Disabled)), BackendKind.Mail);

        after.For(BackendKind.Mail).State.ShouldBe(LinkState.Missing);
        after.For(BackendKind.Groupware).State.ShouldBe(LinkState.Disabled);
    }
}
