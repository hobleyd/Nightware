using System.Text.Json.Nodes;

using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Domain.Kernel;

using WireMock;

namespace Nightware.Adapters.Directory.Tests.Core.Admin;

/// <summary>Request shapes of the organization calls (Keycloak 26.8 admin REST "Organizations").</summary>
public sealed class KeycloakAdminClientOrganizationTests : IDisposable
{
    private const string OrgId = "0b7f3a52-9c41-4d6e-8a2f-5e1c9d3b7a10";
    private const string UserId = "5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11";
    private const string GroupId = "c3d9e2f1-0a4b-4c5d-8e6f-7a8b9c0d1e2f";
    private readonly KeycloakStub _keycloak = new();

    public KeycloakAdminClientOrganizationTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdapterHost Host(int retries = 2) =>
        AdapterHost.CreateConsole(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = retries.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static string Users(int count, int offset = 0) =>
        new JsonArray([.. Enumerable.Range(offset, count).Select(static i => (JsonNode)new JsonObject { ["id"] = $"u{i}", ["username"] = $"user{i}", ["enabled"] = true })]).ToJsonString();

    private static string Orgs(int count, int offset = 0) =>
        new JsonArray([.. Enumerable.Range(offset, count).Select(static i => (JsonNode)new JsonObject { ["id"] = $"o{i}", ["name"] = $"Org {i}", ["alias"] = $"org{i}" })]).ToJsonString();

    private static JsonObject Body(RequestMessage request) => JsonNode.Parse(request.Body!)!.AsObject();

    [Fact]
    public async Task Lists_organizations_brief_following_every_page()
    {
        _keycloak.GivenAdmin("/organizations", 200, Orgs(100), query: new Dictionary<string, string> { ["first"] = "0" });
        _keycloak.GivenAdmin("/organizations", 200, Orgs(3, 100), query: new Dictionary<string, string> { ["first"] = "100" });
        await using AdapterHost host = Host();

        IReadOnlyList<KeycloakOrganization> orgs = (await host.Get<IKeycloakAdminClient>().ListOrganizationsAsync(Ct)).Value;

        orgs.Count.ShouldBe(103);
        IReadOnlyList<RequestMessage> requests = _keycloak.Admin("GET", "/organizations");
        requests.Count.ShouldBe(2);
        requests.ShouldAllBe(static r => r.Query!["max"].Single() == "100" && r.Query["briefRepresentation"].Single() == "true");
        requests.Select(static r => r.Query!["first"].Single()).ShouldBe(["0", "100"]);
    }

    [Fact]
    public async Task A_short_first_page_is_the_last()
    {
        _keycloak.GivenAdmin("/organizations", 200, Fixture.Load("organizations-brief.json"));
        await using AdapterHost host = Host();

        IReadOnlyList<KeycloakOrganization> orgs = (await host.Get<IKeycloakAdminClient>().ListOrganizationsAsync(Ct)).Value;

        orgs.Select(static o => o.Alias).ShouldBe(["nightware", "acme"]);
        orgs[1].Domains!.Select(static d => d.Name).ShouldBe(["acme.example", "Acme-Mail.example", "not a domain"]);
        orgs[1].Domains![1].Verified.ShouldBeTrue();
        _keycloak.Admin("GET", "/organizations").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_failing_later_page_fails_the_list()
    {
        _keycloak.GivenAdmin("/organizations", 200, Orgs(100), query: new Dictionary<string, string> { ["first"] = "0" });
        _keycloak.GivenAdmin("/organizations", 403, query: new Dictionary<string, string> { ["first"] = "100" });
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().ListOrganizationsAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Searches_organizations_exactly()
    {
        _keycloak.GivenAdmin("/organizations", 200, "[]");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().SearchOrganizationsAsync("acme.example", exact: true, Ct)).Value.ShouldBeEmpty();

        IDictionary<string, WireMock.Types.WireMockList<string>> query = _keycloak.Admin("GET", "/organizations").Single().Query!;
        query["search"].ShouldBe(["acme.example"]);
        query["exact"].ShouldBe(["true"]);
        query["briefRepresentation"].ShouldBe(["true"]);
    }

    [Fact]
    public async Task Gets_an_organization_and_a_missing_one_is_not_found_as_an_entity()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, Fixture.Load("organization-nightware.json"));
        _keycloak.GivenAdmin("/organizations/ghost", 404);
        await using AdapterHost host = Host();
        IKeycloakAdminClient client = host.Get<IKeycloakAdminClient>();

        KeycloakOrganization org = (await client.GetOrganizationAsync(OrgId, Ct)).Value;
        Result<KeycloakOrganization> missing = await client.GetOrganizationAsync("ghost", Ct);

        org.Name.ShouldBe("Nightware");
        org.Alias.ShouldBe("nightware");
        org.Domains!.Single().Name.ShouldBe("nightware.localhost");
        missing.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        missing.Error.Message.ShouldContain("entity");
    }

    [Fact]
    public async Task Creates_an_organization_and_reads_its_id_from_the_location()
    {
        _keycloak.GivenAdmin("/organizations", 201, method: "POST", location: $"{_keycloak.Url}/admin/realms/nightware/organizations/new-org-id");
        await using AdapterHost host = Host();

        string id = (await host.Get<IKeycloakAdminClient>().CreateOrganizationAsync(
            new KeycloakOrganization { Name = "Acme", Alias = "acme", Domains = [new KeycloakOrganizationDomain { Name = "acme.example" }] }, Ct)).Value;

        id.ShouldBe("new-org-id");
        JsonObject body = Body(_keycloak.Admin("POST", "/organizations").Single());
        body["name"]!.GetValue<string>().ShouldBe("Acme");
        body["alias"]!.GetValue<string>().ShouldBe("acme");
        body["enabled"]!.GetValue<bool>().ShouldBeTrue();
        body["domains"]![0]!["name"]!.GetValue<string>().ShouldBe("acme.example");
        body.ContainsKey("id").ShouldBeFalse();
    }

    [Fact]
    public async Task A_created_response_without_a_location_is_backend_unavailable()
    {
        _keycloak.GivenAdmin("/organizations", 201, method: "POST");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().CreateOrganizationAsync(new KeycloakOrganization { Name = "Acme" }, Ct))
            .Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Theory]
    [InlineData(409, """{"errorMessage":"A organization with the same alias already exists"}""", ErrorKind.Conflict)]
    [InlineData(400, null, ErrorKind.Validation)]
    public async Task Organization_create_errors(int status, string? body, ErrorKind expected)
    {
        _keycloak.GivenAdmin("/organizations", status, body ?? Fixture.Load("admin-error-domain-linked.json"), method: "POST");
        await using AdapterHost host = Host();

        Result<string> result = await host.Get<IKeycloakAdminClient>().CreateOrganizationAsync(new KeycloakOrganization { Name = "Acme" }, Ct);

        result.Error!.Kind.ShouldBe(expected);
        _keycloak.Admin("POST", "/organizations").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Updates_an_organization_by_read_modify_write_preserving_attributes_and_alias()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, Fixture.Load("organization-nightware.json"));
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 204, method: "PUT");
        await using AdapterHost host = Host();

        KeycloakOrganization updated = (await host.Get<IKeycloakAdminClient>().UpdateOrganizationAsync(
            OrgId,
            org =>
            {
                org["domains"]!.AsArray().Add(new JsonObject { ["name"] = "second.example" });
                return true;
            },
            Ct)).Value;

        updated.Domains!.Select(static d => d.Name).ShouldBe(["nightware.localhost", "second.example"]);
        JsonObject body = Body(_keycloak.Admin("PUT", $"/organizations/{OrgId}").Single());
        body["alias"]!.GetValue<string>().ShouldBe("nightware");
        body["attributes"]!["billing"]![0]!.GetValue<string>().ShouldBe("internal");
        body["description"]!.GetValue<string>().ShouldBe("Bootstrap entity");
    }

    [Fact]
    public async Task An_organization_update_that_changes_nothing_never_puts()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, Fixture.Load("organization-nightware.json"));
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().UpdateOrganizationAsync(OrgId, static _ => false, Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.Admin("PUT", $"/organizations/{OrgId}").ShouldBeEmpty();
    }

    [Fact]
    public async Task An_organization_put_is_not_retried()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, Fixture.Load("organization-nightware.json"));
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 503, method: "PUT");
        await using AdapterHost host = Host(retries: 3);

        (await host.Get<IKeycloakAdminClient>().UpdateOrganizationAsync(OrgId, static _ => true, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);

        _keycloak.Admin("PUT", $"/organizations/{OrgId}").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Lists_members_with_full_representations_across_pages()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/members", 200, Users(100), query: new Dictionary<string, string> { ["first"] = "0" });
        _keycloak.GivenAdmin($"/organizations/{OrgId}/members", 200, Users(1, 100), query: new Dictionary<string, string> { ["first"] = "100" });
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().ListOrganizationMembersAsync(OrgId, Ct)).Value.Count.ShouldBe(101);

        _keycloak.Admin("GET", $"/organizations/{OrgId}/members")
            .ShouldAllBe(static r => r.Query!["briefRepresentation"].Single() == "false" && r.Query["max"].Single() == "100");
    }

    [Fact]
    public async Task Adds_a_member_with_the_user_id_as_a_json_string()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/members", 201, method: "POST");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().AddOrganizationMemberAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();

        RequestMessage post = _keycloak.Admin("POST", $"/organizations/{OrgId}/members").Single();
        post.Body.ShouldBe($"\"{UserId}\"");
        post.Headers!["Content-Type"].Single().ShouldStartWith("application/json");
    }

    [Fact]
    public async Task Adding_an_existing_member_is_success()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/members", 409, """{"errorMessage":"User is already a member of the organization."}""", method: "POST");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().AddOrganizationMemberAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();
        host.Logs.Lines.ShouldNotContain(static l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(400, ErrorKind.Validation)]
    [InlineData(403, ErrorKind.Forbidden)]
    [InlineData(404, ErrorKind.NotFound)]
    public async Task Add_member_errors(int status, ErrorKind expected)
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/members", status, method: "POST");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().AddOrganizationMemberAsync(OrgId, UserId, Ct)).Error!.Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task Lists_a_users_organizations()
    {
        _keycloak.GivenAdmin($"/organizations/members/{UserId}/organizations", 200, Fixture.Load("organizations-brief.json"));
        await using AdapterHost host = Host();

        IReadOnlyList<KeycloakOrganization> orgs = (await host.Get<IKeycloakAdminClient>().ListMemberOrganizationsAsync(UserId, Ct)).Value;

        orgs.Select(static o => o.Id).ShouldBe([OrgId, "6a1e8d27-3f5b-4c90-b7d4-2e8f1a6c9b33"]);
        _keycloak.Admin("GET", $"/organizations/members/{UserId}/organizations").Single().Query!["briefRepresentation"].ShouldBe(["true"]);
    }

    [Fact]
    public async Task Lists_a_members_organization_groups_and_a_non_member_is_not_found()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/members/{UserId}/groups", 200, $$"""[{"id":"{{GroupId}}","name":"admins","path":"/admins"}]""");
        _keycloak.GivenAdmin($"/organizations/{OrgId}/members/stranger/groups", 404);
        await using AdapterHost host = Host();
        IKeycloakAdminClient client = host.Get<IKeycloakAdminClient>();

        KeycloakGroup group = (await client.ListMemberOrganizationGroupsAsync(OrgId, UserId, Ct)).Value.ShouldHaveSingleItem();
        Result<IReadOnlyList<KeycloakGroup>> stranger = await client.ListMemberOrganizationGroupsAsync(OrgId, "stranger", Ct);

        group.Id.ShouldBe(GroupId);
        group.Name.ShouldBe("admins");
        group.ParentId.ShouldBeNull();
        stranger.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        stranger.Error.Message.ShouldContain("not a member");
    }

    [Fact]
    public async Task Lists_top_level_groups_without_a_search_across_pages()
    {
        string groups = new JsonArray([.. Enumerable.Range(0, 100).Select(static i => (JsonNode)new JsonObject { ["id"] = $"g{i}", ["name"] = $"team{i}", ["parentId"] = "internal" })]).ToJsonString();
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups", 200, groups, query: new Dictionary<string, string> { ["first"] = "0" });
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups", 200, """[{"id":"ga","name":"admins","parentId":"internal","path":"/admins"}]""", query: new Dictionary<string, string> { ["first"] = "100" });
        await using AdapterHost host = Host();

        IReadOnlyList<KeycloakGroup> all = (await host.Get<IKeycloakAdminClient>().ListOrganizationTopLevelGroupsAsync(OrgId, Ct)).Value;

        all.Count.ShouldBe(101);
        all[^1].ParentId.ShouldBe("internal");
        IReadOnlyList<RequestMessage> requests = _keycloak.Admin("GET", $"/organizations/{OrgId}/groups");
        requests.Count.ShouldBe(2);
        requests.ShouldAllBe(static r => !r.Query!.ContainsKey("search") && !r.Query.ContainsKey("q") && r.Query["briefRepresentation"].Single() == "true");
    }

    [Fact]
    public async Task Top_level_groups_of_a_missing_organization_are_not_found_as_an_entity()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups", 404);
        await using AdapterHost host = Host();

        Result<IReadOnlyList<KeycloakGroup>> result = await host.Get<IKeycloakAdminClient>().ListOrganizationTopLevelGroupsAsync(OrgId, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        result.Error.Message.ShouldContain("entity");
    }

    [Fact]
    public async Task Creates_an_organization_group()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups", 201, method: "POST", location: $"{_keycloak.Url}/admin/realms/nightware/organizations/{OrgId}/groups/{GroupId}");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().CreateOrganizationGroupAsync(OrgId, "admins", Ct)).Value.ShouldBe(GroupId);

        _keycloak.Admin("POST", $"/organizations/{OrgId}/groups").Single().Body.ShouldBe("""{"name":"admins"}""");
    }

    [Fact]
    public async Task Creating_a_taken_group_name_is_conflict()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups", 409, """{"errorMessage":"Top level group named 'admins' already exists."}""", method: "POST");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().CreateOrganizationGroupAsync(OrgId, "admins", Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public async Task Lists_group_members_with_full_representations_across_pages()
    {
        string path = $"/organizations/{OrgId}/groups/{GroupId}/members";
        _keycloak.GivenAdmin(path, 200, Users(100), query: new Dictionary<string, string> { ["first"] = "0" });
        _keycloak.GivenAdmin(path, 200, "[]", query: new Dictionary<string, string> { ["first"] = "100" });
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().ListOrganizationGroupMembersAsync(OrgId, GroupId, Ct)).Value.Count.ShouldBe(100);

        _keycloak.Admin("GET", path).Count.ShouldBe(2);
        _keycloak.Admin("GET", path).ShouldAllBe(static r => r.Query!["briefRepresentation"].Single() == "false");
    }

    [Fact]
    public async Task Adds_a_group_member_with_put_and_an_existing_one_is_success()
    {
        string path = $"/organizations/{OrgId}/groups/{GroupId}/members/{UserId}";
        _keycloak.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(KeycloakStub.AdminPath + path).UsingPut())
            .InScenario("join").WillSetStateTo("joined")
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(204));
        _keycloak.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(KeycloakStub.AdminPath + path).UsingPut())
            .InScenario("join").WhenStateIs("joined")
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(409).WithBody("""{"errorMessage":"User is already a member of the group"}"""));
        await using AdapterHost host = Host();
        IKeycloakAdminClient client = host.Get<IKeycloakAdminClient>();

        (await client.AddOrganizationGroupMemberAsync(OrgId, GroupId, UserId, Ct)).IsSuccess.ShouldBeTrue();
        (await client.AddOrganizationGroupMemberAsync(OrgId, GroupId, UserId, Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.Admin("PUT", path).Count.ShouldBe(2);
        _keycloak.Admin("PUT", path).ShouldAllBe(static r => string.IsNullOrEmpty(r.Body));
    }

    [Fact]
    public async Task Adding_a_non_member_to_a_group_is_validation()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups/{GroupId}/members/{UserId}", 400, """{"errorMessage":"User is not member of the organization"}""", method: "PUT");
        await using AdapterHost host = Host();

        Result result = await host.Get<IKeycloakAdminClient>().AddOrganizationGroupMemberAsync(OrgId, GroupId, UserId, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Message.ShouldContain("not member of the organization");
    }

    [Fact]
    public async Task Removes_a_group_member_with_delete()
    {
        string path = $"/organizations/{OrgId}/groups/{GroupId}/members/{UserId}";
        _keycloak.GivenAdmin(path, 204, method: "DELETE");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().RemoveOrganizationGroupMemberAsync(OrgId, GroupId, UserId, Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("DELETE", path).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Reads_and_adds_group_realm_role_mappings()
    {
        string path = $"/organizations/{OrgId}/groups/{GroupId}/role-mappings/realm";
        _keycloak.GivenAdmin(path, 200, """[{"id":"r1","name":"nightware-entity-admin","composite":false}]""");
        _keycloak.GivenAdmin(path, 204, method: "POST");
        await using AdapterHost host = Host();
        IKeycloakAdminClient client = host.Get<IKeycloakAdminClient>();

        (await client.GetOrganizationGroupRealmRolesAsync(OrgId, GroupId, Ct)).Value.ShouldHaveSingleItem().Name.ShouldBe("nightware-entity-admin");
        (await client.AddOrganizationGroupRealmRolesAsync(OrgId, GroupId, [new KeycloakRole { Id = "r1", Name = "nightware-entity-admin" }], Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.Admin("POST", path).Single().Body.ShouldBe("""[{"id":"r1","name":"nightware-entity-admin"}]""");
    }

    [Fact]
    public async Task Organization_and_group_ids_are_escaped()
    {
        _keycloak.GivenAdmin("/organizations/a%2Fb/groups/c%3Fd/members/e%23f", 204, method: "PUT");
        await using AdapterHost host = Host();

        await host.Get<IKeycloakAdminClient>().AddOrganizationGroupMemberAsync("a/b", "c?d", "e#f", Ct);

        _keycloak.AdminRequests.Single().RequestMessage!.Url.ShouldEndWith("/organizations/a%2Fb/groups/c%3Fd/members/e%23f");
    }
}
