using Nightware.Application.Common.Query;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Directory;

public sealed class ListUsersTests
{
    [Fact]
    public async Task Passes_the_query_to_the_directory()
    {
        var directory = Substitute.For<IUserDirectory>();
        var query = new ItemQuery { Top = 999, SkipToken = "t" };
        var page = new Page<DirectoryUser>([new DirectoryUser("1", "Sam", "sam@example.com")], "next");
        directory.ListAsync(query, Arg.Any<CancellationToken>()).Returns(page);

        (await new ListUsers(directory).ExecuteAsync(query, TestContext.Current.CancellationToken)).Value.ShouldBe(page);
    }

    [Fact]
    public async Task Directory_failures_pass_through()
    {
        var directory = Substitute.For<IUserDirectory>();
        directory.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(Error.Validation("no $filter"));

        (await new ListUsers(directory).ExecuteAsync(ItemQuery.Default, TestContext.Current.CancellationToken)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }
}
