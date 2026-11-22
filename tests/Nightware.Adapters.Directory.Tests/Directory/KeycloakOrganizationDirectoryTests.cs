using Nightware.Adapters.Directory.Directory;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Directory;

namespace Nightware.Adapters.Directory.Tests.Directory;

/// <summary>Phase 21: the caller's Keycloak organizations as Graph organizations.</summary>
public sealed class KeycloakOrganizationDirectoryTests : IDisposable
{
    private const string UserId = "5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11";

    private readonly KeycloakStub _keycloak = new();

    public KeycloakOrganizationDirectoryTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdapterHost Host() => AdapterHost.Create(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = "0" });

    [Fact]
    public async Task Is_registered_as_the_port()
    {
        await using AdapterHost host = Host();

        host.Scoped<IOrganizationDirectory>().ShouldBeOfType<KeycloakOrganizationDirectory>();
    }

    [Fact]
    public async Task Lists_the_callers_enabled_organizations_with_their_domains_in_name_order()
    {
        _keycloak.GivenAdmin(
            $"/organizations/members/{UserId}/organizations",
            200,
            """
            [{"id":"org-1","name":"Acme","alias":"acme","enabled":true,"domains":[{"name":"Acme.net"},{"name":"acme.example"}]},
             {"id":"org-2","name":"Old","alias":"old","enabled":false,"domains":[]}]
            """);
        await using AdapterHost host = Host();

        var organizations = (await host.Scoped<IOrganizationDirectory>().ListAsync(Ct)).Value;

        organizations.Count.ShouldBe(1);
        (organizations[0].Id, organizations[0].DisplayName).ShouldBe(("org-1", "Acme"));
        organizations[0].Domains.ShouldBe(["acme.example", "acme.net"]);
    }

    [Fact]
    public async Task A_caller_Keycloak_does_not_know_belongs_to_none()
    {
        _keycloak.GivenAdmin($"/organizations/members/{UserId}/organizations", 404, """{"error":"not found"}""");
        await using AdapterHost host = Host();

        (await host.Scoped<IOrganizationDirectory>().ListAsync(Ct)).Value.ShouldBeEmpty();
    }
}
