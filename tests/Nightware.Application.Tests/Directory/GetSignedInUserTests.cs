using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Directory;

public sealed class GetSignedInUserTests
{
    private readonly IUserContext _user = Substitute.For<IUserContext>();
    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();

    public GetSignedInUserTests() =>
        _user.Principal.Returns(new Principal("sub-alice", "alice", "alice@example.com", "Alice Anderson"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GetSignedInUser UseCase => new(_user, _directory);

    [Fact]
    public async Task Looks_the_caller_up_by_their_subject()
    {
        var alice = new DirectoryUser("sub-alice", "Alice A.", "alice@example.com") { Mail = "alice@example.com", JobTitle = "CTO" };
        _directory.GetAsync("sub-alice", Arg.Any<CancellationToken>()).Returns(alice);

        (await UseCase.ExecuteAsync(Ct)).Value.ShouldBe(alice);
    }

    [Fact]
    public async Task A_directory_entry_without_mail_takes_the_token_address()
    {
        _directory.GetAsync("sub-alice", Arg.Any<CancellationToken>()).Returns(new DirectoryUser("sub-alice", "Alice", "alice"));

        (await UseCase.ExecuteAsync(Ct)).Value.Mail.ShouldBe("alice@example.com");
    }

    [Fact]
    public async Task A_caller_unknown_to_the_directory_gets_a_profile_from_the_token()
    {
        _directory.GetAsync("sub-alice", Arg.Any<CancellationToken>()).Returns(Error.NotFound("nope"));

        var me = (await UseCase.ExecuteAsync(Ct)).Value;

        me.Id.ShouldBe("sub-alice");
        me.DisplayName.ShouldBe("Alice Anderson");
        me.Mail.ShouldBe("alice@example.com");
        me.UserPrincipalName.ShouldBe("alice@example.com", "a bare login name is not a UPN the client can use");
    }

    [Fact]
    public async Task An_address_shaped_login_is_kept_as_the_upn()
    {
        _user.Principal.Returns(new Principal("sub-alice", "alice@corp.example.com", "alice@example.com", "Alice"));
        _directory.GetAsync("sub-alice", Arg.Any<CancellationToken>()).Returns(Error.NotFound("nope"));

        (await UseCase.ExecuteAsync(Ct)).Value.UserPrincipalName.ShouldBe("alice@corp.example.com");
    }

    [Theory]
    [InlineData(ErrorKind.BackendUnavailable)]
    [InlineData(ErrorKind.Forbidden)]
    [InlineData(ErrorKind.Throttled)]
    public async Task Other_directory_failures_are_not_masked(ErrorKind kind)
    {
        _directory.GetAsync("sub-alice", Arg.Any<CancellationToken>()).Returns(new Error(kind, "c", "m"));

        (await UseCase.ExecuteAsync(Ct)).Error!.Kind.ShouldBe(kind);
    }
}
