using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.Administration;

public sealed class KeycloakAdminScopeSourceTests : IDisposable
{
    private const string UserId = "user-1";
    private const string Nightware = "0b7f3a52-9c41-4d6e-8a2f-5e1c9d3b7a10";
    private const string Acme = "6a1e8d27-3f5b-4c90-b7d4-2e8f1a6c9b33";
    private const string RolesPath = $"/users/{UserId}/role-mappings/realm/composite";
    private const string OrgsPath = $"/organizations/members/{UserId}/organizations";
    private readonly KeycloakStub _keycloak = new();

    private const string ClientRolesPath = $"/users/{UserId}/role-mappings/clients/rm-uuid/composite";

    public KeycloakAdminScopeSourceTests()
    {
        _keycloak.GivenServiceAccountToken();
        _keycloak.GivenAdmin("/clients", 200, """[{"id":"rm-uuid","clientId":"realm-management"}]""");
        _keycloak.GivenAdmin(ClientRolesPath, 200, "[]");
        _keycloak.GivenAdmin($"/users/{UserId}", 200, $$"""{"id":"{{UserId}}","username":"u","enabled":true}""");
    }

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdapterHost Host(int retries = 2) =>
        AdapterHost.CreateConsole(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = retries.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    /// <summary>The org has a top-level <c>admins</c> group with id <c>{orgId}-admins</c>; the user is in <paramref name="memberGroups"/>.</summary>
    private void GivenGroups(string orgId, string memberGroups)
    {
        _keycloak.GivenAdmin($"/organizations/{orgId}/groups", 200, $$"""[{"id":"{{orgId}}-admins","name":"admins","parentId":"{{orgId}}-internal"}]""");
        _keycloak.GivenAdmin($"/organizations/{orgId}/members/{UserId}/groups", 200, memberGroups);
    }

    [Fact]
    public async Task A_global_admin_with_no_entities()
    {
        _keycloak.GivenAdmin(RolesPath, 200, """[{"id":"r1","name":"default-roles-nightware"},{"id":"r2","name":"nightware-global-admin"}]""");
        _keycloak.GivenAdmin(OrgsPath, 200, "[]");
        await using AdapterHost host = Host();

        AdminScope scope = (await host.Scoped<IAdminScopeSource>().ResolveAsync(UserId, Ct)).Value;

        scope.IsGlobal.ShouldBeTrue();
        scope.EntityIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_entity_admin_of_one_of_two_entities()
    {
        _keycloak.GivenAdmin(RolesPath, 200, """[{"id":"r3","name":"nightware-entity-admin"}]""");
        _keycloak.GivenAdmin(OrgsPath, 200, Fixture.Load("organizations-brief.json"));
        GivenGroups(Nightware, """[{"id":"g1","name":"staff"}]""");
        GivenGroups(Acme, $$"""[{"id":"{{Acme}}-admins","name":"admins","path":"/admins"}]""");
        await using AdapterHost host = Host();

        AdminScope scope = (await host.Scoped<IAdminScopeSource>().ResolveAsync(UserId, Ct)).Value;

        scope.IsGlobal.ShouldBeFalse(); // the entity-admin role alone is only a hint
        scope.EntityIds.ShouldBe([Acme]);
        _keycloak.Admin("GET", OrgsPath).Single().Query!["briefRepresentation"].ShouldBe(["true"]);
    }

    [Fact]
    public async Task A_member_who_left_between_calls_is_not_an_admin_of_it()
    {
        _keycloak.GivenAdmin(RolesPath, 200, "[]");
        _keycloak.GivenAdmin(OrgsPath, 200, Fixture.Load("organizations-brief.json"));
        GivenGroups(Nightware, "[]");
        _keycloak.GivenAdmin($"/organizations/{Nightware}/members/{UserId}/groups", 404, priority: 1);
        GivenGroups(Acme, $$"""[{"id":"{{Acme}}-admins","name":"admins"}]""");
        await using AdapterHost host = Host();

        AdminScope scope = (await host.Scoped<IAdminScopeSource>().ResolveAsync(UserId, Ct)).Value;

        scope.EntityIds.ShouldBe([Acme]);
    }

    [Fact]
    public async Task A_plain_user_has_no_scope()
    {
        _keycloak.GivenAdmin(RolesPath, 200, """[{"id":"r1","name":"default-roles-nightware"}]""");
        _keycloak.GivenAdmin(OrgsPath, 200, $"[{Fixture.Load("organization-nightware.json")}]");
        GivenGroups(Nightware, "[]");
        await using AdapterHost host = Host();

        AdminScope scope = (await host.Scoped<IAdminScopeSource>().ResolveAsync(UserId, Ct)).Value;

        scope.IsGlobal.ShouldBeFalse();
        scope.EntityIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_user_is_not_found()
    {
        _keycloak.GivenAdmin(RolesPath, 404);
        await using AdapterHost host = Host();

        (await host.Scoped<IAdminScopeSource>().ResolveAsync(UserId, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_nested_group_named_admins_does_not_count()
    {
        _keycloak.GivenAdmin(RolesPath, 200, "[]");
        _keycloak.GivenAdmin(OrgsPath, 200, $"[{Fixture.Load("organization-nightware.json")}]");
        GivenGroups(Nightware, """[{"id":"g-nested","name":"admins","parentId":"g-team"}]""");
        await using AdapterHost host = Host();

        (await host.Scoped<IAdminScopeSource>().ResolveAsync(UserId, Ct)).Value.EntityIds.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("roles")]
    [InlineData("orgs")]
    [InlineData("top-level-groups")]
    [InlineData("member-groups")]
    public async Task Any_backend_failure_fails_the_resolution_rather_than_narrowing_it(string failing)
    {
        _keycloak.GivenAdmin(RolesPath, failing == "roles" ? 503 : 200, "[]");
        _keycloak.GivenAdmin(OrgsPath, failing == "orgs" ? 503 : 200, $"[{Fixture.Load("organization-nightware.json")}]");
        _keycloak.GivenAdmin($"/organizations/{Nightware}/groups", failing == "top-level-groups" ? 503 : 200, $$"""[{"id":"{{Nightware}}-admins","name":"admins"}]""");
        _keycloak.GivenAdmin($"/organizations/{Nightware}/members/{UserId}/groups", failing == "member-groups" ? 503 : 200, "[]");
        await using AdapterHost host = Host(retries: 0);

        (await host.Scoped<IAdminScopeSource>().ResolveAsync(UserId, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Requires_a_user_id()
    {
        await using AdapterHost host = Host();

        await Should.ThrowAsync<ArgumentException>(() => host.Scoped<IAdminScopeSource>().ResolveAsync(" ", Ct));
    }

    [Fact]
    public async Task A_holder_of_any_keycloak_administration_role_administers_the_directory()
    {
        _keycloak.GivenAdmin(RolesPath, 200, "[]");
        _keycloak.GivenAdmin(OrgsPath, 200, "[]");
        _keycloak.GivenAdmin(ClientRolesPath, 200, """[{"id":"c1","name":"view-users"}]""", priority: 1);
        await using AdapterHost host = Host();

        AdminScope scope = (await host.Scoped<IAdminScopeSource>().ResolveAsync(UserId, Ct)).Value;

        scope.AdministersDirectory.ShouldBeTrue();
        scope.IsServiceAccount.ShouldBeFalse();
        _keycloak.Admin("GET", "/clients").Single().Query!["clientId"].ShouldBe(["realm-management"]);
    }

    [Fact]
    public async Task A_service_account_administers_the_directory()
    {
        _keycloak.GivenAdmin(RolesPath, 200, "[]");
        _keycloak.GivenAdmin(OrgsPath, 200, "[]");
        _keycloak.GivenAdmin($"/users/{UserId}", 200, $$"""{"id":"{{UserId}}","username":"service-account-x","enabled":true,"serviceAccountClientId":"x"}""", priority: 1);
        await using AdapterHost host = Host();

        AdminScope scope = (await host.Scoped<IAdminScopeSource>().ResolveAsync(UserId, Ct)).Value;
        scope.AdministersDirectory.ShouldBeTrue();
        scope.IsServiceAccount.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_query_clients_the_scope_is_an_error_not_an_ordinary_user()
    {
        _keycloak.GivenAdmin(RolesPath, 200, "[]");
        _keycloak.GivenAdmin(OrgsPath, 200, "[]");
        _keycloak.GivenAdmin("/clients", 403, priority: 1);
        await using AdapterHost host = Host();

        (await host.Scoped<IAdminScopeSource>().ResolveAsync(UserId, Ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Without_view_clients_the_client_id_comes_from_our_own_service_accounts_mappings()
    {
        // Keycloak lists no clients to an account with query-clients only; the console does not hold view-clients.
        _keycloak.GivenAdmin(RolesPath, 200, "[]");
        _keycloak.GivenAdmin(OrgsPath, 200, "[]");
        _keycloak.GivenAdmin("/clients", 200, "[]", priority: 1);
        _keycloak.GivenAdmin("/users", 200, """[{"id":"sa-1","username":"service-account-nightware-admin","enabled":true}]""");
        _keycloak.GivenAdmin("/users/sa-1/role-mappings", 200, """{"clientMappings":{"realm-management":{"id":"rm-uuid","client":"realm-management","mappings":[{"id":"x","name":"view-users"}]}}}""");
        _keycloak.GivenAdmin(ClientRolesPath, 200, """[{"id":"c1","name":"realm-admin"}]""", priority: 1);
        await using AdapterHost host = Host();

        (await host.Scoped<IAdminScopeSource>().ResolveAsync(UserId, Ct)).Value.AdministersDirectory.ShouldBeTrue();
        _keycloak.Admin("GET", "/users").Single().Query!["username"].ShouldBe(["service-account-nightware-admin"]);
    }

    [Fact]
    public async Task A_disabled_user_reports_their_account_disabled()
    {
        _keycloak.GivenAdmin(RolesPath, 200, """[{"id":"r2","name":"nightware-global-admin"}]""");
        _keycloak.GivenAdmin(OrgsPath, 200, "[]");
        _keycloak.GivenAdmin($"/users/{UserId}", 200, $$"""{"id":"{{UserId}}","username":"u","enabled":false}""", priority: 1);
        await using AdapterHost host = Host();

        AdminScope scope = (await host.Scoped<IAdminScopeSource>().ResolveAsync(UserId, Ct)).Value;

        scope.IsGlobal.ShouldBeTrue(); // the roles are still held, so targets stay protected
        scope.AccountEnabled.ShouldBeFalse();
    }
}
