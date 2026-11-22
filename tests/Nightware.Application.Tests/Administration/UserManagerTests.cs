using Nightware.Application.Administration;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

/// <summary><see cref="SetUserManager"/> (phase 21) and the manager <see cref="UserAccess"/> loads with a user.</summary>
public sealed class UserManagerTests
{
    private readonly AdminTestBed _bed = new();

    public UserManagerTests()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example");
        _bed.AddMailbox("carol@acme.example", "m-carol");
        _bed.AddCloudUser("carol", "carol@acme.example");
        _bed.AddUser("u-sara", "sara", "sara@acme.example");
        _bed.Identities.SetManagerAsync(default!, default, default).ReturnsForAnyArgs(Result.Success());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SetUserManager UseCase => new(_bed.Actor, _bed.UserAccess(), _bed.Identities, _bed.Entities, _bed.Audit);

    [Fact]
    public async Task Sets_the_manager_named_by_address_or_username_and_audits_it()
    {
        (await UseCase.ExecuteAsync("u-carol", " sara@acme.example ", Ct)).IsSuccess.ShouldBeTrue();
        _bed.Audit.Single("user.manager.set").Succeeded.ShouldBeTrue();
        (await UseCase.ExecuteAsync("u-carol", "sara", Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Identities.Received(2).SetManagerAsync("u-carol", "u-sara", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Blank_removes_the_manager()
    {
        (await UseCase.ExecuteAsync("u-carol", "  ", Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Identities.Received(1).SetManagerAsync("u-carol", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nobody_unknown_self_or_outside_the_entity_can_be_the_manager()
    {
        _bed.Identities.FindByEmailAsync("ghost@acme.example", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));
        _bed.AddUser("u-olly", "olly", "olly@other.example", entities: [AdminTestBed.Other]);

        (await UseCase.ExecuteAsync("u-carol", "ghost@acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await UseCase.ExecuteAsync("u-carol", "carol", Ct)).Error!.Message.ShouldBe("Nobody can be their own manager.");
        (await UseCase.ExecuteAsync("u-carol", "olly@other.example", Ct)).Error!.Message.ShouldContain("same entity");
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetManagerAsync(default!, default, Ct);
    }

    [Fact]
    public async Task A_choice_that_would_make_a_loop_is_refused()
    {
        // Carol manages Dan, who manages Sara: Sara can't then manage Carol.
        _bed.Update(new IdentityAccount("u-sara", "sara", "sara@acme.example", true) { ManagerId = "u-dan" });
        _bed.Identities.FindByEmailAsync("sara@acme.example", Arg.Any<CancellationToken>())
            .Returns(new IdentityAccount("u-sara", "sara", "sara@acme.example", true) { ManagerId = "u-dan" });
        _bed.Update(new IdentityAccount("u-dan", "dan", "dan@acme.example", true) { ManagerId = "u-carol" });

        (await UseCase.ExecuteAsync("u-carol", "sara@acme.example", Ct)).Error!.Message.ShouldContain("loop");
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetManagerAsync(default!, default, Ct);
    }

    [Fact]
    public async Task A_chain_through_a_deleted_user_ends_there()
    {
        _bed.Identities.FindByEmailAsync("sara@acme.example", Arg.Any<CancellationToken>())
            .Returns(new IdentityAccount("u-sara", "sara", "sara@acme.example", true) { ManagerId = "u-gone" });
        _bed.Identities.GetAsync("u-gone", Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));

        (await UseCase.ExecuteAsync("u-carol", "sara@acme.example", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_user_out_of_reach_is_refused()
    {
        _bed.ActAsNobody();

        (await UseCase.ExecuteAsync("u-carol", "sara", Ct)).IsSuccess.ShouldBeFalse();
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetManagerAsync(default!, default, Ct);
    }

    [Fact]
    public async Task The_user_loads_with_their_manager_when_it_still_exists()
    {
        _bed.Update(new IdentityAccount("u-carol", "carol", "carol@acme.example", true) { ManagerId = "u-sara" });

        (await _bed.UserAccess().LoadWithLinksAsync("u-carol", Ct)).Value.Manager!.Email.ShouldBe("sara@acme.example");

        _bed.Identities.GetAsync("u-sara", Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));
        (await _bed.UserAccess().LoadWithLinksAsync("u-carol", Ct)).Value.Manager.ShouldBeNull();
    }
}
