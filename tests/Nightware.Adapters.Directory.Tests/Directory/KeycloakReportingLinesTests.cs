using Nightware.Adapters.Directory.Directory;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Common.Query;
using Nightware.Application.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.Directory;

/// <summary>Phase 21 reporting lines from the <c>nightware_manager</c> user attribute.</summary>
public sealed class KeycloakReportingLinesTests : IDisposable
{
    private const string Bob = "219829b5-85df-4e5f-90d5-4d366e50648c";
    private const string Alice = "6b57d6e4-d5f5-424a-b931-0b8e191c34ea";

    private readonly KeycloakStub _keycloak = new();

    public KeycloakReportingLinesTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdapterHost Host() => AdapterHost.Create(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = "0" });

    private void GivenUser(string id, string username, string? manager = null, int status = 200)
    {
        var attributes = manager is null ? "{}" : $$"""{"nightware_manager":["{{manager}}"]}""";
        _keycloak.GivenAdmin($"/users/{id}", status,
            $$$"""{"id":"{{{id}}}","username":"{{{username}}}","email":"{{{username}}}@acme.example","firstName":"{{{username}}}","enabled":true,"attributes":{{{attributes}}}}""");
    }

    [Fact]
    public async Task Is_registered_as_the_port()
    {
        await using AdapterHost host = Host();

        host.Scoped<IReportingLines>().ShouldBeOfType<KeycloakReportingLines>();
    }

    [Fact]
    public async Task The_manager_is_the_user_the_attribute_names()
    {
        GivenUser(Bob, "bob", manager: Alice);
        GivenUser(Alice, "alice");
        await using AdapterHost host = Host();

        var manager = (await host.Scoped<IReportingLines>().GetManagerAsync(Bob, Ct)).Value;

        manager.Id.ShouldBe(Alice);
        manager.Mail.ShouldBe("alice@acme.example");
    }

    [Fact]
    public async Task No_attribute_is_no_manager()
    {
        GivenUser(Bob, "bob");
        await using AdapterHost host = Host();

        (await host.Scoped<IReportingLines>().GetManagerAsync(Bob, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_manager_deleted_since_is_no_manager()
    {
        GivenUser(Bob, "bob", manager: Alice);
        _keycloak.GivenAdmin($"/users/{Alice}", 404, """{"error":"User not found"}""");
        await using AdapterHost host = Host();

        (await host.Scoped<IReportingLines>().GetManagerAsync(Bob, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_caller_known_only_from_the_token_has_no_manager()
    {
        await using AdapterHost host = Host();

        (await host.Scoped<IReportingLines>().GetManagerAsync("not-a-keycloak-id", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _keycloak.AdminRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Direct_reports_are_found_by_attribute_search_and_paged()
    {
        _keycloak.GivenAdmin(
            "/users",
            200,
            $$$"""
            [{"id":"u-1","username":"bob","email":"bob@acme.example","enabled":true,"attributes":{"nightware_manager":["{{{Alice}}}"]}},
             {"id":"u-2","username":"carol","email":"carol@acme.example","enabled":true,"attributes":{"nightware_manager":["{{{Alice}}}"]}}]
            """,
            query: new Dictionary<string, string> { ["q"] = $"nightware_manager:{Alice}", ["enabled"] = "true" });
        await using AdapterHost host = Host();

        var page = (await host.Scoped<IReportingLines>().ListDirectReportsAsync(Alice, new ItemQuery { Top = 1 }, Ct)).Value;

        page.Items.Select(static u => u.Mail).ShouldBe(["bob@acme.example"]);
        page.SkipToken.ShouldBe("1");
        var request = _keycloak.Admin("GET", "/users").Single();
        request.Query!["max"].ToString().ShouldBe("2");
        request.Query["briefRepresentation"].ToString().ShouldBe("false");
    }

    [Fact]
    public async Task A_search_hit_whose_attribute_differs_is_not_a_report()
    {
        // Keycloak's q matches attribute values loosely; only the exact id counts.
        _keycloak.GivenAdmin("/users", 200, """[{"id":"u-1","username":"bob","enabled":true,"attributes":{"nightware_manager":["someone-else"]}}]""");
        await using AdapterHost host = Host();

        (await host.Scoped<IReportingLines>().ListDirectReportsAsync(Alice, new ItemQuery(), Ct)).Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Direct_reports_refuse_a_filter()
    {
        await using AdapterHost host = Host();

        (await host.Scoped<IReportingLines>().ListDirectReportsAsync(Alice, new ItemQuery { Skip = 2 }, Ct)).Error!.Target.ShouldBe("$skip");
    }
}
