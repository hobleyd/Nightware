using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Directory;

public sealed class DirectoryObjectsTests
{
    private readonly IUserContext _user = Substitute.For<IUserContext>();
    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();

    public DirectoryObjectsTests() => _user.Principal.Returns(new Principal("sub-alice", "alice@example.com", "alice@example.com", "Alice"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Me_is_the_caller_without_asking_the_directory()
    {
        (await DirectoryObjects.UserIdAsync(_user, _directory, null, Ct)).Value.ShouldBe("sub-alice");
        await _directory.DidNotReceiveWithAnyArgs().GetAsync(default!, Ct);
    }

    [Fact]
    public async Task A_named_user_is_looked_up_by_id_upn_or_address()
    {
        _directory.GetAsync("sam@example.com", Arg.Any<CancellationToken>()).Returns(new DirectoryUser("id-sam", "Sam", "sam@example.com"));

        (await DirectoryObjects.UserIdAsync(_user, _directory, " sam@example.com ", Ct)).Value.ShouldBe("id-sam");
    }

    [Theory]
    [InlineData(ErrorKind.NotFound, ErrorKind.NotFound)]
    [InlineData(ErrorKind.Validation, ErrorKind.NotFound)]
    [InlineData(ErrorKind.BackendUnavailable, ErrorKind.BackendUnavailable)]
    public async Task Any_miss_is_a_404_and_failures_pass_through(ErrorKind backend, ErrorKind expected)
    {
        _directory.GetAsync("x", Arg.Any<CancellationToken>()).Returns(new Error(backend, "c", "m"));

        (await DirectoryObjects.UserIdAsync(_user, _directory, "x", Ct)).Error!.Kind.ShouldBe(expected);
        (await DirectoryObjects.UserIdAsync(_user, _directory, "  ", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public void A_store_miss_becomes_Graph_s_directory_miss()
    {
        var miss = DirectoryObjects.OrMiss(Result<int>.Failure(Error.NotFound("none")), "manager");

        miss.Error!.Code.ShouldBe("Request_ResourceNotFound");
        miss.Error.Message.ShouldBe("Resource 'manager' does not exist or one of its queried reference-property objects are not present.");
        DirectoryObjects.OrMiss(Result<int>.Failure(Error.Forbidden("no")), "manager").Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        DirectoryObjects.OrMiss(Result<int>.Success(3), "manager").Value.ShouldBe(3);
    }
}
