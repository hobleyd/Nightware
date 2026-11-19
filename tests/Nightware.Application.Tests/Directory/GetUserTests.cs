using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Directory;

public sealed class GetUserTests
{
    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();

    private readonly ISharedMailboxDirectory _shared = Substitute.For<ISharedMailboxDirectory>();

    public GetUserTests() =>
        _shared.GetAsync(default!, Ct).ReturnsForAnyArgs(Error.NotFound("not a shared mailbox"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GetUser UseCase => new(_directory, _shared);

    [Fact]
    public async Task Returns_the_directory_user()
    {
        var sam = new DirectoryUser("id-sam", "Sam", "sam@example.com") { Mail = "sam@example.com" };
        _directory.GetAsync("sam@example.com", Arg.Any<CancellationToken>()).Returns(sam);

        (await UseCase.ExecuteAsync(" sam@example.com ", Ct)).Value.ShouldBe(sam);
    }

    [Theory]
    [InlineData(ErrorKind.NotFound, ErrorKind.NotFound)]
    [InlineData(ErrorKind.Validation, ErrorKind.NotFound)]
    [InlineData(ErrorKind.Forbidden, ErrorKind.Forbidden)]
    [InlineData(ErrorKind.BackendUnavailable, ErrorKind.BackendUnavailable)]
    public async Task A_malformed_name_is_a_miss_and_other_failures_pass_through(ErrorKind backend, ErrorKind expected)
    {
        _directory.GetAsync("x", Arg.Any<CancellationToken>()).Returns(new Error(backend, "c", "m"));

        (await UseCase.ExecuteAsync("x", Ct)).Error!.Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task An_address_the_directory_does_not_know_may_be_a_shared_mailbox_the_caller_opens()
    {
        var support = new DirectoryUser("support@example.com", "Support", "support@example.com") { Mail = "support@example.com" };
        _directory.GetAsync(default!, Ct).ReturnsForAnyArgs(Error.NotFound("no user"));
        _shared.GetAsync("support@example.com", Arg.Any<CancellationToken>()).Returns(support);

        (await UseCase.ExecuteAsync("support@example.com", Ct)).Value.ShouldBe(support);
        (await UseCase.ExecuteAsync("nobody@example.com", Ct)).Error!.Message.ShouldBe("no user");

        // Only an address can be a shared mailbox, and only after the directory missed.
        (await UseCase.ExecuteAsync("id-x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        await _shared.DidNotReceive().GetAsync("id-x", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ErrorKind.Forbidden, ErrorKind.NotFound)]
    [InlineData(ErrorKind.Unsupported, ErrorKind.NotFound)]
    [InlineData(ErrorKind.BackendUnavailable, ErrorKind.BackendUnavailable)]
    public async Task A_mail_server_that_cannot_say_is_still_a_miss_unless_it_failed(ErrorKind mail, ErrorKind expected)
    {
        _directory.GetAsync(default!, Ct).ReturnsForAnyArgs(Error.NotFound("no user"));
        _shared.GetAsync(default!, Ct).ReturnsForAnyArgs(new Error(mail, "c", "m"));

        (await UseCase.ExecuteAsync("support@example.com", Ct)).Error!.Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task A_directory_failure_is_not_hidden_behind_the_shared_mailbox_lookup()
    {
        _directory.GetAsync(default!, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("keycloak down"));

        (await UseCase.ExecuteAsync("support@example.com", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        await _shared.DidNotReceiveWithAnyArgs().GetAsync(default!, Ct);
    }

    [Fact]
    public async Task A_blank_name_is_not_found_without_asking_the_directory()
    {
        (await UseCase.ExecuteAsync("  ", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        await _directory.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
