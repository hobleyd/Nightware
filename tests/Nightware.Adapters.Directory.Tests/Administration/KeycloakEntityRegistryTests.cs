using System.Text.Json.Nodes;

using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.Administration;

public sealed class KeycloakEntityRegistryTests : IDisposable
{
    private const string OrgId = "0b7f3a52-9c41-4d6e-8a2f-5e1c9d3b7a10";
    private const string AcmeId = "6a1e8d27-3f5b-4c90-b7d4-2e8f1a6c9b33";
    private const string UserId = "5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11";
    private const string GroupId = "c3d9e2f1-0a4b-4c5d-8e6f-7a8b9c0d1e2f";
    private readonly KeycloakStub _keycloak = new();

    public KeycloakEntityRegistryTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MailDomain Domain(string name) => MailDomain.TryParse(name, out MailDomain? d) ? d : throw new ArgumentException(name);

    private AdapterHost Host() => AdapterHost.CreateConsole(_keycloak.Url);

    private void GivenNoDomainOwner() => _keycloak.GivenAdmin("/organizations", 200, "[]");

    [Fact]
    public async Task Lists_entities_sorted_by_name()
    {
        _keycloak.GivenAdmin("/organizations", 200, Fixture.Load("organizations-brief.json"));
        await using AdapterHost host = Host();

        IReadOnlyList<Entity> entities = (await host.Scoped<IEntityRegistry>().ListAsync(Ct)).Value;

        entities.Select(static e => e.Alias).ShouldBe(["acme", "nightware"]);
        entities[0].Domains.Select(static d => d.Name).ShouldBe(["acme-mail.example", "acme.example"]);
    }

    [Fact]
    public async Task Gets_an_entity()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, Fixture.Load("organization-nightware.json"));
        await using AdapterHost host = Host();

        Entity entity = (await host.Scoped<IEntityRegistry>().GetAsync(OrgId, Ct)).Value;

        entity.ShouldBe(new Entity(OrgId, "nightware", "Nightware", entity.Domains));
        entity.Domains.ShouldBe([Domain("nightware.localhost")]);
    }

    [Fact]
    public async Task Creates_an_entity_after_checking_the_domain_is_free()
    {
        GivenNoDomainOwner();
        _keycloak.GivenAdmin("/organizations", 201, method: "POST", location: $"{_keycloak.Url}/admin/realms/nightware/organizations/{AcmeId}");
        await using AdapterHost host = Host();

        Entity entity = (await host.Scoped<IEntityRegistry>().CreateAsync(new NewEntity("Acme", "acme", Domain("acme.example")), Ct)).Value;

        entity.Id.ShouldBe(AcmeId);
        entity.Alias.ShouldBe("acme");
        entity.Domains.ShouldBe([Domain("acme.example")]);
        WireMock.RequestMessage search = _keycloak.Admin("GET", "/organizations").Single();
        search.Query!["search"].ShouldBe(["acme.example"]);
        search.Query["exact"].ShouldBe(["true"]);
        JsonObject body = JsonNode.Parse(_keycloak.Admin("POST", "/organizations").Single().Body!)!.AsObject();
        body["name"]!.GetValue<string>().ShouldBe("Acme");
        body["alias"]!.GetValue<string>().ShouldBe("acme");
        body["domains"]!.AsArray().Single()!["name"]!.GetValue<string>().ShouldBe("acme.example");
    }

    [Fact]
    public async Task A_domain_another_entity_owns_is_conflict_and_nothing_is_created()
    {
        _keycloak.GivenAdmin("/organizations", 200, Fixture.Load("organizations-brief.json"));
        await using AdapterHost host = Host();

        Result<Entity> result = await host.Scoped<IEntityRegistry>().CreateAsync(new NewEntity("Acme 2", "acme2", Domain("ACME.example")), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        result.Error.Message.ShouldContain("Acme");
        result.Error.Target.ShouldBe("domain");
        _keycloak.Admin("POST", "/organizations").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_search_hit_by_name_only_is_not_a_domain_owner()
    {
        _keycloak.GivenAdmin("/organizations", 200, """[{"id":"x","name":"acme.example","alias":"weird","domains":[{"name":"other.example"}]}]""");
        _keycloak.GivenAdmin("/organizations", 201, method: "POST", location: "/admin/realms/nightware/organizations/new");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().CreateAsync(new NewEntity("Acme", "acme", Domain("acme.example")), Ct)).Value.Id.ShouldBe("new");
    }

    [Fact]
    public async Task Keycloaks_domain_already_linked_400_is_conflict()
    {
        GivenNoDomainOwner();
        _keycloak.GivenAdmin("/organizations", 400, Fixture.Load("admin-error-domain-linked.json"), method: "POST");
        await using AdapterHost host = Host();

        Result<Entity> result = await host.Scoped<IEntityRegistry>().CreateAsync(new NewEntity("Acme", "acme", Domain("acme.example")), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        result.Error.Target.ShouldBe("domain");
    }

    [Fact]
    public async Task A_taken_alias_is_conflict()
    {
        GivenNoDomainOwner();
        _keycloak.GivenAdmin("/organizations", 409, """{"errorMessage":"A organization with the same alias already exists"}""", method: "POST");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().CreateAsync(new NewEntity("Acme", "acme", Domain("acme.example")), Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public async Task Other_validation_errors_stay_validation()
    {
        GivenNoDomainOwner();
        _keycloak.GivenAdmin("/organizations", 400, """{"errorMessage":"Organizations are disabled"}""", method: "POST");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().CreateAsync(new NewEntity("Acme", "acme", Domain("acme.example")), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task A_failing_domain_check_stops_the_create()
    {
        _keycloak.GivenAdmin("/organizations", 403);
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().CreateAsync(new NewEntity("Acme", "acme", Domain("acme.example")), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _keycloak.Admin("POST", "/organizations").ShouldBeEmpty();
    }

    [Fact]
    public async Task Adds_a_domain_by_read_modify_write()
    {
        GivenNoDomainOwner();
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, Fixture.Load("organization-nightware.json"));
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 204, method: "PUT");
        await using AdapterHost host = Host();

        Entity entity = (await host.Scoped<IEntityRegistry>().AddDomainAsync(OrgId, Domain("second.example"), Ct)).Value;

        entity.Domains.Select(static d => d.Name).ShouldBe(["nightware.localhost", "second.example"]);
        JsonObject body = JsonNode.Parse(_keycloak.Admin("PUT", $"/organizations/{OrgId}").Single().Body!)!.AsObject();
        body["domains"]!.AsArray().Select(static d => d!["name"]!.GetValue<string>()).ShouldBe(["nightware.localhost", "second.example"]);
        body["alias"]!.GetValue<string>().ShouldBe("nightware");
        body["attributes"]!["billing"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Adding_a_domain_the_entity_already_has_is_a_no_op()
    {
        // The exact search finds the entity itself — not a conflict.
        _keycloak.GivenAdmin("/organizations", 200, $"[{Fixture.Load("organization-nightware.json")}]");
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, Fixture.Load("organization-nightware.json"));
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().AddDomainAsync(OrgId, Domain("NIGHTWARE.localhost"), Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.Admin("PUT", $"/organizations/{OrgId}").ShouldBeEmpty();
    }

    [Fact]
    public async Task Adding_a_domain_another_entity_owns_is_conflict_without_a_write()
    {
        _keycloak.GivenAdmin("/organizations", 200, Fixture.Load("organizations-brief.json"));
        await using AdapterHost host = Host();

        Result<Entity> result = await host.Scoped<IEntityRegistry>().AddDomainAsync(OrgId, Domain("acme.example"), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        _keycloak.Admin("PUT", $"/organizations/{OrgId}").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_racing_domain_clash_on_put_is_conflict()
    {
        GivenNoDomainOwner();
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, Fixture.Load("organization-nightware.json"));
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 400, Fixture.Load("admin-error-domain-linked.json"), method: "PUT");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().AddDomainAsync(OrgId, Domain("acme.example"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public async Task Adding_a_domain_to_a_missing_entity_is_not_found()
    {
        GivenNoDomainOwner();
        _keycloak.GivenAdmin("/organizations/ghost", 404);
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().AddDomainAsync("ghost", Domain("x.example"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Removes_a_domain()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, """{"id":"0b7f3a52-9c41-4d6e-8a2f-5e1c9d3b7a10","name":"Nightware","alias":"nightware","domains":[{"name":"nightware.localhost"},{"name":"Second.example"}]}""");
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 204, method: "PUT");
        await using AdapterHost host = Host();

        Entity entity = (await host.Scoped<IEntityRegistry>().RemoveDomainAsync(OrgId, Domain("second.example"), Ct)).Value;

        entity.Domains.ShouldBe([Domain("nightware.localhost")]);
        JsonNode.Parse(_keycloak.Admin("PUT", $"/organizations/{OrgId}").Single().Body!)!["domains"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Removing_a_domain_forgets_its_spf_and_dmarc_choice()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, """{"id":"0b7f3a52-9c41-4d6e-8a2f-5e1c9d3b7a10","name":"Nightware","alias":"nightware","domains":[{"name":"nightware.localhost"},{"name":"second.example"}],"attributes":{"billing":["internal"],"nightware_spf:second.example":["v=spf1 -all"],"nightware_dmarc:second.example":["v=DMARC1; p=none"],"nightware_spf:nightware.localhost":["v=spf1 mx -all"]}}""");
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 204, method: "PUT");
        await using AdapterHost host = Host();

        Entity entity = (await host.Scoped<IEntityRegistry>().RemoveDomainAsync(OrgId, Domain("second.example"), Ct)).Value;

        JsonObject attributes = JsonNode.Parse(_keycloak.Admin("PUT", $"/organizations/{OrgId}").Single().Body!)!["attributes"]!.AsObject();
        attributes.Select(static a => a.Key).ShouldBe(["billing", "nightware_spf:nightware.localhost"], ignoreOrder: true);
        entity.MailPolicyFor(Domain("nightware.localhost")).Spf!.Value.ShouldBe("v=spf1 mx -all");
    }

    [Fact]
    public async Task Sets_a_domains_spf_and_dmarc_as_organization_attributes_keeping_the_others()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, Fixture.Load("organization-nightware.json"));
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 204, method: "PUT");
        await using AdapterHost host = Host();
        DomainMailPolicy policy = new(SpfRecord.Parse("v=spf1 mx -all").Value, DmarcRecord.Parse("v=DMARC1; p=quarantine").Value);

        Entity entity = (await host.Scoped<IEntityRegistry>().SetMailPolicyAsync(OrgId, Domain("nightware.localhost"), policy, Ct)).Value;

        entity.MailPolicyFor(Domain("nightware.localhost")).ShouldBe(policy);
        JsonObject body = JsonNode.Parse(_keycloak.Admin("PUT", $"/organizations/{OrgId}").Single().Body!)!.AsObject();
        body["attributes"]!["nightware_spf:nightware.localhost"]!.AsArray().Single()!.GetValue<string>().ShouldBe("v=spf1 mx -all");
        body["attributes"]!["nightware_dmarc:nightware.localhost"]!.AsArray().Single()!.GetValue<string>().ShouldBe("v=DMARC1; p=quarantine");
        body["attributes"]!["billing"].ShouldNotBeNull();
        body["domains"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Returning_a_domain_to_the_mail_servers_records_removes_its_attributes()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, """{"id":"0b7f3a52-9c41-4d6e-8a2f-5e1c9d3b7a10","name":"Nightware","alias":"nightware","domains":[{"name":"nightware.localhost"}],"attributes":{"nightware_spf:nightware.localhost":["v=spf1 -all"]}}""");
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 204, method: "PUT");
        await using AdapterHost host = Host();

        Entity entity = (await host.Scoped<IEntityRegistry>().SetMailPolicyAsync(OrgId, Domain("nightware.localhost"), DomainMailPolicy.MailServerDefault, Ct)).Value;

        entity.MailPolicies.ShouldBeEmpty();
        JsonNode.Parse(_keycloak.Admin("PUT", $"/organizations/{OrgId}").Single().Body!)!["attributes"]!.AsObject().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Setting_the_policy_already_stored_writes_nothing()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, """{"id":"0b7f3a52-9c41-4d6e-8a2f-5e1c9d3b7a10","name":"Nightware","alias":"nightware","domains":[{"name":"nightware.localhost"}],"attributes":{"nightware_spf:nightware.localhost":["v=spf1 -all"]}}""");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().SetMailPolicyAsync(OrgId, Domain("nightware.localhost"), new DomainMailPolicy(SpfRecord.Parse("v=spf1 -all").Value, null), Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.Admin("PUT", $"/organizations/{OrgId}").ShouldBeEmpty();
    }

    [Fact]
    public async Task Setting_a_policy_for_a_domain_the_entity_lacks_is_not_found_without_a_write()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, Fixture.Load("organization-nightware.json"));
        await using AdapterHost host = Host();

        Result<Entity> result = await host.Scoped<IEntityRegistry>().SetMailPolicyAsync(OrgId, Domain("other.example"), DomainMailPolicy.MailServerDefault, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        result.Error.Target.ShouldBe("domain");
        _keycloak.Admin("PUT", $"/organizations/{OrgId}").ShouldBeEmpty();
    }

    [Fact]
    public async Task Stores_reads_and_removes_the_sealed_dns_token_as_an_organization_attribute()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, Fixture.Load("organization-nightware.json"));
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 204, method: "PUT");
        await using AdapterHost host = Host();
        IEntityRegistry registry = host.Scoped<IEntityRegistry>();

        (await registry.GetSealedDnsTokenAsync(OrgId, Ct)).Value.ShouldBeNull();
        (await registry.GetAsync(OrgId, Ct)).Value.HasDnsToken.ShouldBeFalse();
        (await registry.SetSealedDnsTokenAsync(OrgId, "CfDJ8-sealed", Ct)).IsSuccess.ShouldBeTrue();

        JsonObject body = JsonNode.Parse(_keycloak.Admin("PUT", $"/organizations/{OrgId}").Single().Body!)!.AsObject();
        body["attributes"]!["nightware_dns_token:cloudflare"]!.AsArray().Single()!.GetValue<string>().ShouldBe("CfDJ8-sealed");
        body["attributes"]!["billing"].ShouldNotBeNull();
    }

    [Fact]
    public async Task An_entity_with_a_sealed_token_says_so_and_returns_it()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, """{"id":"0b7f3a52-9c41-4d6e-8a2f-5e1c9d3b7a10","name":"Nightware","alias":"nightware","domains":[{"name":"nightware.localhost"}],"attributes":{"nightware_dns_token:cloudflare":["CfDJ8-sealed"]}}""");
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 204, method: "PUT");
        await using AdapterHost host = Host();
        IEntityRegistry registry = host.Scoped<IEntityRegistry>();

        (await registry.GetAsync(OrgId, Ct)).Value.HasDnsToken.ShouldBeTrue();
        (await registry.GetSealedDnsTokenAsync(OrgId, Ct)).Value.ShouldBe("CfDJ8-sealed");
        (await registry.SetSealedDnsTokenAsync(OrgId, null, Ct)).IsSuccess.ShouldBeTrue();
        JsonNode.Parse(_keycloak.Admin("PUT", $"/organizations/{OrgId}").Single().Body!)!["attributes"]!.AsObject().Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_sealed_token_longer_than_an_attribute_value_is_refused_without_a_write()
    {
        await using AdapterHost host = Host();

        Result result = await host.Scoped<IEntityRegistry>().SetSealedDnsTokenAsync(OrgId, new string('x', 256), Ct);

        result.Error!.Target.ShouldBe("dnsToken");
        _keycloak.Admin("PUT", $"/organizations/{OrgId}").ShouldBeEmpty();
    }

    [Fact]
    public async Task Removing_a_domain_the_entity_lacks_is_a_no_op()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, Fixture.Load("organization-nightware.json"));
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().RemoveDomainAsync(OrgId, Domain("other.example"), Ct)).Value.Domains.Count.ShouldBe(1);
        _keycloak.Admin("PUT", $"/organizations/{OrgId}").ShouldBeEmpty();
    }

    [Fact]
    public async Task Removing_from_an_organization_without_a_domains_array_is_a_no_op()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}", 200, """{"id":"0b7f3a52-9c41-4d6e-8a2f-5e1c9d3b7a10","name":"Nightware","alias":"nightware"}""");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().RemoveDomainAsync(OrgId, Domain("other.example"), Ct)).Value.Domains.ShouldBeEmpty();
        _keycloak.Admin("PUT", $"/organizations/{OrgId}").ShouldBeEmpty();
    }

    [Fact]
    public async Task Lists_members_with_aliases_and_markers()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/members", 200, Fixture.Load("organization-members.json"));
        await using AdapterHost host = Host();

        IReadOnlyList<IdentityAccount> members = (await host.Scoped<IEntityRegistry>().ListMembersAsync(OrgId, Ct)).Value;

        members.Select(static m => m.Username).ShouldBe(["alice", "bob"]);
        members[0].Aliases.ShouldBe(["a@nightware.localhost", "postmaster@nightware.localhost"]);
        members[1].Enabled.ShouldBeFalse();
        members[1].MailDeletedAt.ShouldBe(new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.Zero));
        members[1].GroupwareDeletedAt.ShouldBeNull();
        members[1].FirstName.ShouldBeNull();
        _keycloak.Admin("GET", $"/organizations/{OrgId}/members").Single().Query!["briefRepresentation"].ShouldBe(["false"]);
    }

    [Fact]
    public async Task Gets_a_users_memberships_as_entity_ids()
    {
        _keycloak.GivenAdmin($"/organizations/members/{UserId}/organizations", 200, Fixture.Load("organizations-brief.json"));
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().GetMembershipsAsync(UserId, Ct)).Value.ShouldBe([OrgId, AcmeId]);
    }

    [Fact]
    public async Task Memberships_of_a_missing_user_are_not_found()
    {
        _keycloak.GivenAdmin($"/organizations/members/{UserId}/organizations", 404);
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().GetMembershipsAsync(UserId, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Adds_a_member_idempotently()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/members", 409, """{"errorMessage":"User is already a member of the organization."}""", method: "POST");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().AddMemberAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("POST", $"/organizations/{OrgId}/members").Single().Body.ShouldBe($"\"{UserId}\"");
    }

    [Fact]
    public async Task Lists_admins_from_the_admins_group()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups", 200, $$"""[{"id":"{{GroupId}}","name":"admins","path":"/admins"}]""");
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups/{GroupId}/members", 200, Fixture.Load("organization-members.json"));
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().ListAdminsAsync(OrgId, Ct)).Value.Select(static a => a.Username).ShouldBe(["alice", "bob"]);
    }

    [Fact]
    public async Task An_entity_without_an_admins_group_has_no_admins()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups", 200, """[{"id":"g-team","name":"team","parentId":"org-internal"}]""");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityRegistry>().ListAdminsAsync(OrgId, Ct)).Value.ShouldBeEmpty();
        _keycloak.AdminRequests.ShouldNotContain(static e => e.RequestMessage!.Path!.EndsWith("/members", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Grant_and_revoke_go_through_the_admins_group()
    {
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups", 200, $$"""[{"id":"{{GroupId}}","name":"admins"}]""");
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups/{GroupId}/members/{UserId}", 204, method: "PUT");
        _keycloak.GivenAdmin($"/organizations/{OrgId}/members/{UserId}/groups", 200, $$"""[{"id":"{{GroupId}}","name":"admins"}]""");
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups/{GroupId}/members/{UserId}", 204, method: "DELETE");
        await using AdapterHost host = Host();
        IEntityRegistry registry = host.Scoped<IEntityRegistry>();

        (await registry.GrantAdminAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();
        (await registry.RevokeAdminAsync(OrgId, UserId, Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.Admin("PUT", $"/organizations/{OrgId}/groups/{GroupId}/members/{UserId}").Count.ShouldBe(1);
        _keycloak.Admin("DELETE", $"/organizations/{OrgId}/groups/{GroupId}/members/{UserId}").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Arguments_are_required()
    {
        await using AdapterHost host = Host();
        IEntityRegistry registry = host.Scoped<IEntityRegistry>();

        await Should.ThrowAsync<ArgumentException>(() => registry.GetAsync(" ", Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => registry.CreateAsync(null!, Ct));
        await Should.ThrowAsync<ArgumentException>(() => registry.AddMemberAsync(OrgId, "", Ct));
        await Should.ThrowAsync<ArgumentException>(() => registry.GrantAdminAsync("", UserId, Ct));
        _keycloak.AdminRequests.ShouldBeEmpty();
    }
}
