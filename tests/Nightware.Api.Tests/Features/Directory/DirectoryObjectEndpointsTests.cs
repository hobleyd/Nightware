using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Query;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Directory;

/// <summary>Phase 21: manager, direct reports, memberOf, groups and members, the organization.</summary>
public sealed class DirectoryObjectEndpointsTests : IAsyncLifetime
{
    private static readonly DirectoryUser Sara = new("u-sara", "Sara Davis", "sara@example.com") { Mail = "sara@example.com", JobTitle = "Finance VP" };

    private static readonly DirectoryGroup Finance = new("g-1", "Finance", "finance")
    {
        Mail = "finance@example.com",
        Aliases = ["accounts@example.com"],
        SecurityEnabled = true,
    };

    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();
    private readonly IReportingLines _lines = Substitute.For<IReportingLines>();
    private readonly IGroupDirectory _groups = Substitute.For<IGroupDirectory>();
    private readonly IOrganizationDirectory _organizations = Substitute.For<IOrganizationDirectory>();
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await TestHost.StartAsync(services => services
            .AddSingleton(_directory).AddSingleton(_lines).AddSingleton(_groups).AddSingleton(_organizations));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> GetAsync(string path) => _host.AuthorizedClient().GetAsync(path, Ct);

    [Fact]
    public async Task The_manager_is_a_typed_directory_object()
    {
        _lines.GetManagerAsync("user-1", Arg.Any<CancellationToken>()).Returns(Sara);

        var body = await (await GetAsync("/v1.0/me/manager")).ReadObjectAsync();

        body["@odata.context"]!.GetValue<string>().ShouldBe(TestHost.GraphBase + "/$metadata#directoryObjects/$entity");
        body["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.user");
        body.Str("jobTitle").ShouldBe("Finance VP");
    }

    [Fact]
    public async Task No_manager_is_Graph_s_404()
    {
        _lines.GetManagerAsync("user-1", Arg.Any<CancellationToken>()).Returns(Error.NotFound("none"));

        var response = await GetAsync("/v1.0/me/manager");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadObjectAsync())["error"]!["code"]!.GetValue<string>().ShouldBe("Request_ResourceNotFound");
    }

    [Fact]
    public async Task Another_users_manager_and_direct_reports_are_looked_up_by_address()
    {
        _directory.GetAsync("sam@example.com", Arg.Any<CancellationToken>()).Returns(new DirectoryUser("id-sam", "Sam", "sam@example.com"));
        _lines.GetManagerAsync("id-sam", Arg.Any<CancellationToken>()).Returns(Sara);
        _lines.ListDirectReportsAsync("id-sam", Arg.Is<ItemQuery>(q => q.Top == 1), Arg.Any<CancellationToken>())
            .Returns(new Page<DirectoryUser>([Sara], "1"));

        (await (await GetAsync("/v1.0/users/sam%40example.com/manager?$select=mail")).ReadObjectAsync()).Str("mail").ShouldBe("sara@example.com");
        var reports = await (await GetAsync("/v1.0/users/sam@example.com/directReports?$top=1")).ReadObjectAsync();
        reports["value"]!.AsArray().Single()!["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.user");
        reports["@odata.nextLink"]!.GetValue<string>().ShouldEndWith("skiptoken=1");
    }

    [Theory]
    [InlineData("memberOf")]
    [InlineData("transitiveMemberOf")]
    public async Task Member_of_lists_typed_groups(string relationship)
    {
        _groups.ListMemberOfAsync("user-1", Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryGroup>([Finance]));

        var body = await (await GetAsync($"/v1.0/me/{relationship}")).ReadObjectAsync();

        body["@odata.context"]!.GetValue<string>().ShouldBe(TestHost.GraphBase + "/$metadata#directoryObjects");
        var group = body["value"]!.AsArray().Single()!.AsObject();
        group["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.group");
        group["mailEnabled"]!.GetValue<bool>().ShouldBeTrue();
        group["proxyAddresses"]!.AsArray().Select(static p => p!.GetValue<string>()).ShouldBe(["SMTP:finance@example.com", "smtp:accounts@example.com"]);
    }

    [Fact]
    public async Task Groups_are_listed_and_read_without_a_type()
    {
        _groups.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryGroup>([Finance, new DirectoryGroup("g-2", "Board", "board")]));
        _groups.GetAsync("g-1", Arg.Any<CancellationToken>()).Returns(Finance);

        var list = await (await GetAsync("/v1.0/groups")).ReadObjectAsync();
        var one = await (await GetAsync("/v1.0/groups/g-1?$select=displayName,securityEnabled")).ReadObjectAsync();

        var board = list["value"]!.AsArray()[1]!.AsObject();
        board.ContainsKey("@odata.type").ShouldBeFalse();
        board["mail"].ShouldBeNull();
        board["mailEnabled"]!.GetValue<bool>().ShouldBeFalse();
        board["groupTypes"]!.AsArray().ShouldBeEmpty();
        one.Select(static p => p.Key).ShouldBe(["@odata.context", "id", "displayName", "securityEnabled"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("members")]
    [InlineData("transitiveMembers")]
    public async Task A_groups_members_are_typed_users(string relationship)
    {
        _groups.ListMembersAsync("g-1", Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryUser>([Sara]));

        var body = await (await GetAsync($"/v1.0/groups/g-1/{relationship}")).ReadObjectAsync();

        body["value"]!.AsArray().Single()!["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.user");
    }

    [Fact]
    public async Task An_unknown_group_is_Graph_s_404()
    {
        _groups.GetAsync("g-x", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));

        var response = await GetAsync("/v1.0/groups/g-x");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadObjectAsync())["error"]!["code"]!.GetValue<string>().ShouldBe("Request_ResourceNotFound");
    }

    [Fact]
    public async Task The_organization_has_its_domains_as_verified_domains()
    {
        _organizations.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<DirectoryOrganization>>.Success([new DirectoryOrganization("org-1", "Acme", ["acme.example", "acme.net"])]));

        var list = await (await GetAsync("/v1.0/organization")).ReadObjectAsync();
        var one = await (await GetAsync("/v1.0/organization/org-1")).ReadObjectAsync();

        var org = list["value"]!.AsArray().Single()!.AsObject();
        org.Str("displayName").ShouldBe("Acme");
        org.Str("tenantType").ShouldBe("AAD");
        var domains = org["verifiedDomains"]!.AsArray();
        domains.Select(static d => (d!["name"]!.GetValue<string>(), d["isDefault"]!.GetValue<bool>())).ShouldBe([("acme.example", true), ("acme.net", false)]);
        one["@odata.context"]!.GetValue<string>().ShouldBe(TestHost.GraphBase + "/$metadata#organization/$entity");
        (await GetAsync("/v1.0/organization/org-2")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
