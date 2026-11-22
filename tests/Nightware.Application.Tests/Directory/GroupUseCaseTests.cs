using Nightware.Application.Common.Identity;
using Nightware.Application.Common.Query;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Directory;

/// <summary><see cref="ListGroups"/>, <see cref="GetGroup"/>, <see cref="ListGroupMembers"/> and <see cref="ListMemberOf"/>.</summary>
public sealed class GroupUseCaseTests
{
    private static readonly DirectoryGroup Finance = new("g-1", "Finance", "finance") { Mail = "finance@example.com" };

    private readonly IGroupDirectory _groups = Substitute.For<IGroupDirectory>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_the_groups()
    {
        var query = new ItemQuery { Top = 2 };
        _groups.ListAsync(query, Arg.Any<CancellationToken>()).Returns(new Page<DirectoryGroup>([Finance], "2"));

        (await new ListGroups(_groups).ExecuteAsync(query, Ct)).Value.SkipToken.ShouldBe("2");
    }

    [Fact]
    public async Task Gets_a_group_and_a_miss_is_Graph_s_404()
    {
        _groups.GetAsync("g-1", Arg.Any<CancellationToken>()).Returns(Finance);
        _groups.GetAsync("g-x", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));
        var useCase = new GetGroup(_groups);

        (await useCase.ExecuteAsync(" g-1 ", Ct)).Value.ShouldBe(Finance);
        (await useCase.ExecuteAsync("g-x", Ct)).Error!.Message.ShouldContain("'g-x'");
        (await useCase.ExecuteAsync(" ", Ct)).Error!.Code.ShouldBe("Request_ResourceNotFound");
    }

    [Fact]
    public async Task Lists_a_groups_members()
    {
        var sara = new DirectoryUser("u-1", "Sara", "sara@example.com");
        _groups.ListMembersAsync("g-1", Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryUser>([sara]));
        _groups.ListMembersAsync("g-x", Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));
        var useCase = new ListGroupMembers(_groups);

        (await useCase.ExecuteAsync("g-1", new ItemQuery(), Ct)).Value.Items.ShouldBe([sara]);
        (await useCase.ExecuteAsync("g-x", new ItemQuery(), Ct)).Error!.Code.ShouldBe("Request_ResourceNotFound");
        (await useCase.ExecuteAsync("", new ItemQuery(), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Member_of_is_asked_for_the_named_user()
    {
        var user = Substitute.For<IUserContext>();
        user.Principal.Returns(new Principal("sub-alice", "alice@example.com", "alice@example.com", "Alice"));
        var directory = Substitute.For<IUserDirectory>();
        directory.GetAsync("sam@example.com", Arg.Any<CancellationToken>()).Returns(new DirectoryUser("id-sam", "Sam", "sam@example.com"));
        _groups.ListMemberOfAsync("sub-alice", Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryGroup>([Finance]));
        _groups.ListMemberOfAsync("id-sam", Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryGroup>([]));
        var useCase = new ListMemberOf(user, directory, _groups);

        (await useCase.ExecuteAsync(null, new ItemQuery(), Ct)).Value.Items.ShouldBe([Finance]);
        (await useCase.ExecuteAsync("sam@example.com", new ItemQuery(), Ct)).Value.Items.ShouldBeEmpty();
    }
}
