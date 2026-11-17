using Nightware.Adapters.Directory.Administration;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Directory.Tests.Administration;

/// <summary>The entity-admin representation: organization group <c>admins</c> carrying <c>nightware-entity-admin</c>.</summary>
public sealed class KeycloakEntityAdminsTests : IDisposable
{
    private const string OrgId = "org-1";
    private const string UserId = "user-1";
    private const string GroupId = "group-admins";
    private const string GroupsPath = $"/organizations/{OrgId}/groups";
    private const string JoinPath = $"/organizations/{OrgId}/groups/{GroupId}/members/{UserId}";
    private const string MemberGroupsPath = $"/organizations/{OrgId}/members/{UserId}/groups";
    private readonly KeycloakStub _keycloak = new();

    public KeycloakEntityAdminsTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdapterHost Host() => AdapterHost.CreateConsole(_keycloak.Url);

    /// <summary>Top-level organization groups as Keycloak 26.8 returns them: parentId is the org's internal group.</summary>
    private void GivenAdminsGroup() =>
        _keycloak.GivenAdmin(GroupsPath, 200, $$"""[{"id":"{{GroupId}}","name":"admins","path":"/admins","parentId":"org-internal"},{"id":"g-sales","name":"sales","parentId":"org-internal"}]""");

    private void GivenNoAdminsGroupThenCreated()
    {
        // First lookup: none; after the POST, the group exists.
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + GroupsPath).UsingGet())
            .InScenario("group").WhenStateIs("created")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody($$"""[{"id":"{{GroupId}}","name":"admins"}]"""));
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + GroupsPath).UsingGet())
            .AtPriority(20)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("[]"));
    }

    [Fact]
    public async Task Grant_joins_the_existing_group()
    {
        GivenAdminsGroup();
        _keycloak.GivenAdmin(JoinPath, 204, method: "PUT");
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().GrantAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.Admin("POST", GroupsPath).ShouldBeEmpty();
        _keycloak.Admin("PUT", JoinPath).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Grant_is_idempotent_when_already_an_admin()
    {
        GivenAdminsGroup();
        _keycloak.GivenAdmin(JoinPath, 409, """{"errorMessage":"User is already a member of the group"}""", method: "PUT");
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().GrantAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Grant_creates_the_group_with_the_entity_admin_role_on_first_use()
    {
        _keycloak.GivenAdmin(GroupsPath, 200, "[]");
        _keycloak.GivenAdmin(GroupsPath, 201, method: "POST", location: $"/admin/realms/nightware{GroupsPath}/{GroupId}");
        _keycloak.GivenAdmin("/roles/nightware-entity-admin", 200, """{"id":"r-entity","name":"nightware-entity-admin"}""");
        _keycloak.GivenAdmin($"{GroupsPath}/{GroupId}/role-mappings/realm", 204, method: "POST");
        _keycloak.GivenAdmin(JoinPath, 204, method: "PUT");
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().GrantAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.Admin("POST", GroupsPath).Single().Body.ShouldBe("""{"name":"admins"}""");
        _keycloak.Admin("POST", $"{GroupsPath}/{GroupId}/role-mappings/realm").Single().Body.ShouldBe("""[{"id":"r-entity","name":"nightware-entity-admin"}]""");
        _keycloak.Admin("PUT", JoinPath).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_missing_entity_admin_role_is_logged_and_the_grant_still_succeeds()
    {
        _keycloak.GivenAdmin(GroupsPath, 200, "[]");
        _keycloak.GivenAdmin(GroupsPath, 201, method: "POST", location: $"/admin/realms/nightware{GroupsPath}/{GroupId}");
        _keycloak.GivenAdmin("/roles/nightware-entity-admin", 404);
        _keycloak.GivenAdmin(JoinPath, 204, method: "PUT");
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().GrantAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();

        host.Logs.Lines.ShouldContain(static l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("nightware-entity-admin", StringComparison.Ordinal));
        _keycloak.Admin("POST", $"{GroupsPath}/{GroupId}/role-mappings/realm").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_lost_creation_race_uses_the_group_that_won()
    {
        GivenNoAdminsGroupThenCreated();
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + GroupsPath).UsingPost())
            .InScenario("group").WillSetStateTo("created")
            .RespondWith(Response.Create().WithStatusCode(409).WithBody("""{"errorMessage":"Top level group named 'admins' already exists."}"""));
        _keycloak.GivenAdmin(JoinPath, 204, method: "PUT");
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().GrantAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.Admin("GET", GroupsPath).Count.ShouldBe(2);
        _keycloak.Admin("PUT", JoinPath).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_group_creation_fails_the_grant()
    {
        _keycloak.GivenAdmin(GroupsPath, 200, "[]");
        _keycloak.GivenAdmin(GroupsPath, 403, method: "POST");
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().GrantAsync(OrgId, UserId, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _keycloak.Admin("PUT", JoinPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task Granting_a_non_member_is_validation()
    {
        GivenAdminsGroup();
        _keycloak.GivenAdmin(JoinPath, 400, """{"errorMessage":"User is not member of the organization"}""", method: "PUT");
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().GrantAsync(OrgId, UserId, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Revoke_without_an_admins_group_is_a_no_op_that_never_creates_it()
    {
        _keycloak.GivenAdmin(GroupsPath, 200, "[]");
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().RevokeAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.AdminRequests.ShouldAllBe(static e => e.RequestMessage!.Method == "GET");
    }

    [Fact]
    public async Task Revoking_someone_who_is_not_an_admin_writes_nothing()
    {
        GivenAdminsGroup();
        _keycloak.GivenAdmin(MemberGroupsPath, 200, """[{"id":"g-other","name":"sales"}]""");
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().RevokeAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("DELETE", JoinPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task Revoking_a_non_member_of_the_entity_writes_nothing()
    {
        GivenAdminsGroup();
        _keycloak.GivenAdmin(MemberGroupsPath, 404);
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().RevokeAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("DELETE", JoinPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task Revoking_an_admin_leaves_the_group()
    {
        GivenAdminsGroup();
        _keycloak.GivenAdmin(MemberGroupsPath, 200, $$"""[{"id":"{{GroupId}}","name":"admins"}]""");
        _keycloak.GivenAdmin(JoinPath, 204, method: "DELETE");
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().RevokeAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("DELETE", JoinPath).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Revoke_surfaces_a_failing_membership_lookup()
    {
        GivenAdminsGroup();
        _keycloak.GivenAdmin(MemberGroupsPath, 500);
        await using AdapterHost host = AdapterHost.CreateConsole(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = "0" });

        (await host.Scoped<KeycloakEntityAdmins>().RevokeAsync(OrgId, UserId, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _keycloak.Admin("DELETE", JoinPath).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""[{"id":"group-admins","name":"admins","parentId":"org-internal"}]""", true)]
    [InlineData("""[{"id":"g-sales","name":"sales"},{"id":"group-admins","name":"admins"}]""", true)]
    [InlineData("""[{"id":"g-nested","name":"admins","parentId":"g-sales"}]""", false)] // a nested group with the same name
    [InlineData("""[{"id":"g-sales","name":"sales"}]""", false)]
    [InlineData("""[]""", false)]
    public async Task Is_admin_only_through_the_top_level_admins_group_by_id(string memberGroups, bool expected)
    {
        GivenAdminsGroup();
        _keycloak.GivenAdmin(MemberGroupsPath, 200, memberGroups);
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().IsAdminAsync(OrgId, UserId, Ct)).Value.ShouldBe(expected);
    }

    [Fact]
    public async Task Nobody_administers_an_entity_without_an_admins_group()
    {
        _keycloak.GivenAdmin(GroupsPath, 200, """[{"id":"g-sales","name":"sales","parentId":"org-internal"}]""");
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().IsAdminAsync(OrgId, UserId, Ct)).Value.ShouldBeFalse();
        _keycloak.Admin("GET", MemberGroupsPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_former_member_of_the_entity_is_not_an_admin()
    {
        GivenAdminsGroup();
        _keycloak.GivenAdmin(MemberGroupsPath, 404);
        await using AdapterHost host = Host();

        (await host.Scoped<KeycloakEntityAdmins>().IsAdminAsync(OrgId, UserId, Ct)).Value.ShouldBeFalse();
    }

    [Fact]
    public async Task The_admins_group_is_looked_up_among_top_level_groups_not_by_search()
    {
        GivenAdminsGroup();
        _keycloak.GivenAdmin(MemberGroupsPath, 200, "[]");
        await using AdapterHost host = Host();

        await host.Scoped<KeycloakEntityAdmins>().IsAdminAsync(OrgId, UserId, Ct);

        _keycloak.Admin("GET", GroupsPath).Single().Query!.ShouldNotContainKey("search");
    }

    [Fact]
    public async Task Admin_listing_failure_is_surfaced()
    {
        _keycloak.GivenAdmin(GroupsPath, 404);
        await using AdapterHost host = Host();

        Result<IReadOnlyList<Nightware.Adapters.Directory.Core.Admin.KeycloakUser>> result = await host.Scoped<KeycloakEntityAdmins>().ListAsync(OrgId, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        result.Error.Message.ShouldContain("entity");
    }
}
