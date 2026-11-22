using Nightware.Application.Common.Identity;
using Nightware.Application.Common.Query;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Directory;

/// <summary><see cref="GetManager"/> and <see cref="ListDirectReports"/>.</summary>
public sealed class ReportingLineTests
{
    private static readonly DirectoryUser Sara = new("id-sara", "Sara", "sara@example.com");

    private readonly IUserContext _user = Substitute.For<IUserContext>();
    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();
    private readonly IReportingLines _lines = Substitute.For<IReportingLines>();

    public ReportingLineTests()
    {
        _user.Principal.Returns(new Principal("sub-alice", "alice@example.com", "alice@example.com", "Alice"));
        _directory.GetAsync("sam@example.com", Arg.Any<CancellationToken>()).Returns(new DirectoryUser("id-sam", "Sam", "sam@example.com"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_manager_of_me_and_of_a_named_user()
    {
        _lines.GetManagerAsync("sub-alice", Arg.Any<CancellationToken>()).Returns(Sara);
        _lines.GetManagerAsync("id-sam", Arg.Any<CancellationToken>()).Returns(Sara);
        var useCase = new GetManager(_user, _directory, _lines);

        (await useCase.ExecuteAsync(null, Ct)).Value.ShouldBe(Sara);
        (await useCase.ExecuteAsync("sam@example.com", Ct)).Value.ShouldBe(Sara);
    }

    [Fact]
    public async Task No_manager_is_Graph_s_404()
    {
        _lines.GetManagerAsync("sub-alice", Arg.Any<CancellationToken>()).Returns(Error.NotFound("none"));

        (await new GetManager(_user, _directory, _lines).ExecuteAsync(null, Ct)).Error!.Code.ShouldBe("Request_ResourceNotFound");
    }

    [Fact]
    public async Task An_unknown_user_has_no_manager_to_ask_for()
    {
        _directory.GetAsync("nobody@example.com", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no user"));

        (await new GetManager(_user, _directory, _lines).ExecuteAsync("nobody@example.com", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        await _lines.DidNotReceiveWithAnyArgs().GetManagerAsync(default!, Ct);
    }

    [Fact]
    public async Task Direct_reports_of_me_and_of_a_named_user()
    {
        var query = new ItemQuery { Top = 5 };
        _lines.ListDirectReportsAsync("sub-alice", query, Arg.Any<CancellationToken>()).Returns(new Page<DirectoryUser>([Sara]));
        _lines.ListDirectReportsAsync("id-sam", query, Arg.Any<CancellationToken>()).Returns(new Page<DirectoryUser>([]));
        var useCase = new ListDirectReports(_user, _directory, _lines);

        (await useCase.ExecuteAsync(null, query, Ct)).Value.Items.ShouldBe([Sara]);
        (await useCase.ExecuteAsync("sam@example.com", query, Ct)).Value.Items.ShouldBeEmpty();
    }
}
