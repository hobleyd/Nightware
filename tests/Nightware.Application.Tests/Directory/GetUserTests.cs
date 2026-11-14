using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Directory;

public sealed class GetUserTests
{
    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GetUser UseCase => new(_directory);

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
    public async Task A_blank_name_is_not_found_without_asking_the_directory()
    {
        (await UseCase.ExecuteAsync("  ", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        await _directory.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
