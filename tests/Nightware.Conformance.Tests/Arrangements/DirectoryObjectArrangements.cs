using Nightware.Application.Common.Query;
using Nightware.Application.Directory;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 21 directory operations: manager, direct reports, memberOf, groups and members, the organization.</summary>
public sealed class DirectoryObjectArrangements : IConformanceArrangements
{
    private const string Me = "conformance-user";

    private static readonly DirectoryUser Sara = new("7d54cb02-aaa3-4016-9f9c-a4b49422dd9b", "Sara Davis", "sara@example.com")
    {
        Mail = "sara@example.com",
        GivenName = "Sara",
        Surname = "Davis",
        JobTitle = "Finance VP",
    };

    private static readonly DirectoryUser Adele = new("6ea91a8d-e32e-41a1-b7bd-d2d185eed0e0", "Adele Adams", "adele@example.com")
    {
        Mail = "adele@example.com",
        GivenName = "Adele",
        Surname = "Adams",
        JobTitle = "Analyst",
    };

    private static readonly DirectoryUser Lee = new("11111111-2222-3333-4444-555555555555", "Lee Gu", "lee@example.com")
    {
        Mail = "lee@example.com",
        GivenName = "Lee",
        Surname = "Gu",
        BusinessPhones = ["+61 2 9000 0001"],
    };

    private static readonly DirectoryGroup Golf = new("d7797254-3084-44d0-99c9-a3b5ab149538", "Golf Discussion", "golftalk")
    {
        Mail = "golftalk@example.com",
        Aliases = ["golf@example.com"],
    };

    private static readonly DirectoryGroup Finance = new("45b7d2e7-b882-4a80-ba97-10b7a63b8fa4", "Finance", "finance")
    {
        Mail = "finance@example.com",
        SecurityEnabled = true,
    };

    private static readonly DirectoryGroup BoardPapers = new("02bd9fd6-8f93-4758-87c3-1fb73740a315", "Board papers", "board-papers") { SecurityEnabled = true };

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("directory.me.manager.get", static fakes =>
            fakes.For<IReportingLines>().GetManagerAsync(Me, Arg.Any<CancellationToken>()).Returns(Sara));

        yield return new ConformanceArrangement("directory.users.manager.get", static fakes =>
        {
            Sam(fakes);
            fakes.For<IReportingLines>().GetManagerAsync("id-sam", Arg.Any<CancellationToken>()).Returns(Sara);
        });

        yield return new ConformanceArrangement("directory.me.direct-reports.list", static fakes =>
            fakes.For<IReportingLines>()
                .ListDirectReportsAsync(Me, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>())
                .Returns(new Page<DirectoryUser>([Adele, Lee])));

        yield return new ConformanceArrangement("directory.users.direct-reports.list", static fakes =>
        {
            fakes.For<IUserDirectory>().GetAsync("sara@example.com", Arg.Any<CancellationToken>()).Returns(Sara);
            fakes.For<IReportingLines>()
                .ListDirectReportsAsync(Sara.Id, Arg.Is<ItemQuery>(q => q.Top == 1), Arg.Any<CancellationToken>())
                .Returns(new Page<DirectoryUser>([Adele], SkipToken: "1"));
        });

        foreach (var op in new[] { "directory.me.member-of.list", "directory.me.transitive-member-of.list" })
        {
            yield return new ConformanceArrangement(op, static fakes =>
                fakes.For<IGroupDirectory>()
                    .ListMemberOfAsync(Me, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>())
                    .Returns(new Page<DirectoryGroup>([Golf, Finance])));
        }

        foreach (var op in new[] { "directory.users.member-of.list", "directory.users.transitive-member-of.list" })
        {
            yield return new ConformanceArrangement(op, static fakes =>
            {
                Sam(fakes);
                fakes.For<IGroupDirectory>()
                    .ListMemberOfAsync("id-sam", Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>())
                    .Returns(new Page<DirectoryGroup>([BoardPapers]));
            });
        }

        yield return new ConformanceArrangement("directory.groups.list", static fakes =>
            fakes.For<IGroupDirectory>()
                .ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>())
                .Returns(new Page<DirectoryGroup>([Golf, Finance, BoardPapers])));

        yield return new ConformanceArrangement("directory.groups.get", static fakes =>
            fakes.For<IGroupDirectory>().GetAsync(Finance.Id, Arg.Any<CancellationToken>()).Returns(Finance));

        foreach (var op in new[] { "directory.groups.members.list", "directory.groups.transitive-members.list" })
        {
            yield return new ConformanceArrangement(op, static fakes =>
                fakes.For<IGroupDirectory>()
                    .ListMembersAsync(Finance.Id, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>())
                    .Returns(new Page<DirectoryUser>([Sara, Lee])));
        }

        foreach (var op in new[] { "directory.organization.list", "directory.organization.get" })
        {
            yield return new ConformanceArrangement(op, static fakes =>
                fakes.For<IOrganizationDirectory>()
                    .ListAsync(Arg.Any<CancellationToken>())
                    .Returns(Result<IReadOnlyList<DirectoryOrganization>>.Success(
                        [new DirectoryOrganization("84841066-274d-4ec0-a5c1-276be684bdd3", "Example Pty Ltd", ["example.com", "example.net"])])));
        }
    }

    private static void Sam(PortFakes fakes) =>
        fakes.For<IUserDirectory>()
            .GetAsync("sam@example.com", Arg.Any<CancellationToken>())
            .Returns(new DirectoryUser("id-sam", "Sam Attendee", "sam@example.com") { Mail = "sam@example.com" });
}
