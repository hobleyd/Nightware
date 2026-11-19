using System.Text.Json.Nodes;

using Nightware.Adapters.Directory.Administration;
using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.Administration;

/// <summary>
/// Shared mailboxes as subgroups of the organization group <c>mailboxes</c>, named by address, with admin-only
/// attributes; request shapes as for groups (Keycloak 26.8.2 accepts an address as a subgroup name, phase 10 step 0).
/// </summary>
public sealed class KeycloakEntityMailboxesTests : IDisposable
{
    private const string OrgId = "org-1";
    private const string ParentId = "m-parent";
    private const string GroupsParentId = "g-parent";
    private const string MailboxId = "m-support";
    private const string UserId = "u-carol";
    private const string TopLevel = $"/organizations/{OrgId}/groups";
    private const string Children = $"/organizations/{OrgId}/groups/{ParentId}/children";
    private const string MailboxPath = $"/organizations/{OrgId}/groups/{MailboxId}";
    private const string MemberGroups = $"/organizations/{OrgId}/members/{UserId}/groups";
    private const string SupportJson = $$"""
        {"id":"{{MailboxId}}","name":"support@acme.example","path":"/mailboxes/support@acme.example","parentId":"{{ParentId}}","attributes":{
          "nightware_name":["Support desk"],
          "nightware_mailbox_aliases":["help@acme.example","not an address"]} }
        """;

    private readonly KeycloakStub _keycloak = new();

    public KeycloakEntityMailboxesTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdapterHost Host() => AdapterHost.CreateConsole(_keycloak.Url);

    private void GivenParent() =>
        _keycloak.GivenAdmin(TopLevel, 200, $$"""[{"id":"g-admins","name":"admins"},{"id":"{{GroupsParentId}}","name":"groups"},{"id":"{{ParentId}}","name":"mailboxes"}]""");

    private void GivenSupport() => _keycloak.GivenAdmin(MailboxPath, 200, SupportJson);

    [Fact]
    public async Task Is_registered_as_the_port()
    {
        await using AdapterHost host = Host();

        host.Scoped<IEntityMailboxes>().ShouldBeOfType<KeycloakEntityMailboxes>();
    }

    [Fact]
    public async Task A_shared_mailbox_is_read_from_its_name_and_attributes()
    {
        GivenParent();
        GivenSupport();
        await using AdapterHost host = Host();

        SharedMailbox mailbox = (await host.Scoped<IEntityMailboxes>().GetAsync(OrgId, MailboxId, Ct)).Value;

        mailbox.Address.Value.ShouldBe("support@acme.example");
        mailbox.Name.ShouldBe("Support desk");
        mailbox.EntityId.ShouldBe(OrgId);
        mailbox.Aliases.ShouldHaveSingleItem().Value.ShouldBe("help@acme.example");
    }

    [Fact]
    public void A_subgroup_whose_name_is_not_an_address_is_not_a_shared_mailbox()
    {
        KeycloakEntityMailboxes.ToMailbox(OrgId, new KeycloakGroup { Id = "m1", Name = "support@acme.example" })!.Name.ShouldBe("support@acme.example");
        KeycloakEntityMailboxes.ToMailbox(OrgId, new KeycloakGroup { Id = "m2", Name = "Made by hand" }).ShouldBeNull();
        KeycloakEntityMailboxes.ToMailbox(OrgId, new KeycloakGroup { Id = "m3", Name = "Support@Acme.example" }).ShouldBeNull();
    }

    [Fact]
    public async Task A_group_is_never_a_shared_mailbox_whatever_id_is_asked_for()
    {
        GivenParent();
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups/g-fin", 200, $$"""{"id":"g-fin","name":"finance","parentId":"{{GroupsParentId}}"}""");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityMailboxes>().GetAsync(OrgId, "g-fin", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task An_entity_without_the_parent_group_has_no_shared_mailboxes()
    {
        _keycloak.GivenAdmin(TopLevel, 200, """[{"id":"g-admins","name":"admins"}]""");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityMailboxes>().ListAsync(OrgId, Ct)).Value.ShouldBeEmpty();
        (await host.Scoped<IEntityMailboxes>().ListUserMailboxesAsync(OrgId, UserId, Ct)).Value.ShouldBeEmpty();
        (await host.Scoped<IEntityMailboxes>().GetAsync(OrgId, MailboxId, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Listing_reads_each_child_for_its_attributes_and_skips_strays()
    {
        GivenParent();
        _keycloak.GivenAdmin(Children, 200, $$"""[{"id":"{{MailboxId}}","name":"support@acme.example"},{"id":"m-gone","name":"gone@acme.example"},{"id":"m-hand","name":"By Hand"}]""");
        GivenSupport();
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups/m-gone", 404);
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups/m-hand", 200, $$"""{"id":"m-hand","name":"By Hand","parentId":"{{ParentId}}"}""");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityMailboxes>().ListAsync(OrgId, Ct)).Value.ShouldHaveSingleItem().Name.ShouldBe("Support desk");
    }

    [Fact]
    public async Task Creating_makes_the_parent_once_then_the_subgroup_named_by_address_then_writes_attributes()
    {
        _keycloak.GivenAdmin(TopLevel, 200, """[{"id":"g-admins","name":"admins"}]""");
        _keycloak.GivenAdmin(TopLevel, 201, method: "POST", location: $"https://kc/admin/realms/nightware/organizations/{OrgId}/groups/{ParentId}");
        _keycloak.GivenAdmin(Children, 201, method: "POST", location: $"https://kc/admin/realms/nightware{Children}/{MailboxId}");
        _keycloak.GivenAdmin(MailboxPath, 204, method: "PUT");
        await using AdapterHost host = Host();

        SharedMailbox mailbox = (await host.Scoped<IEntityMailboxes>().CreateAsync(OrgId, new NewSharedMailbox(Address("support@acme.example"), "Support") { Aliases = [Address("help@acme.example")] }, Ct)).Value;

        mailbox.Id.ShouldBe(MailboxId);
        Body(_keycloak.Admin("POST", TopLevel).Single())["name"]!.GetValue<string>().ShouldBe("mailboxes");
        Body(_keycloak.Admin("POST", Children).Single())["name"]!.GetValue<string>().ShouldBe("support@acme.example");
        JsonObject put = Body(_keycloak.Admin("PUT", MailboxPath).Single());
        put["name"]!.GetValue<string>().ShouldBe("support@acme.example");
        put["attributes"]!["nightware_name"]![0]!.GetValue<string>().ShouldBe("Support");
        put["attributes"]!["nightware_mailbox_aliases"]![0]!.GetValue<string>().ShouldBe("help@acme.example");
    }

    [Fact]
    public async Task A_taken_address_is_a_conflict_on_the_address()
    {
        GivenParent();
        _keycloak.GivenAdmin(Children, 409, """{"errorMessage":"Sibling group named 'support@acme.example' already exists."}""", method: "POST");
        await using AdapterHost host = Host();

        Error error = (await host.Scoped<IEntityMailboxes>().CreateAsync(OrgId, new NewSharedMailbox(Address("support@acme.example"), "Support"), Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Conflict);
        error.Target.ShouldBe("address");
    }

    [Fact]
    public async Task Deleting_checks_the_subgroup_is_a_shared_mailbox_first()
    {
        GivenParent();
        GivenSupport();
        _keycloak.GivenAdmin(MailboxPath, 204, method: "DELETE");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityMailboxes>().DeleteAsync(OrgId, MailboxId, Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("DELETE", MailboxPath).Count.ShouldBe(1);
        (await host.Scoped<IEntityMailboxes>().DeleteAsync(OrgId, "g-admins", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Adding_a_member_is_a_put_and_removing_one_looks_first()
    {
        _keycloak.GivenAdmin($"{MailboxPath}/members/{UserId}", 204, method: "PUT");
        _keycloak.GivenAdmin($"{MailboxPath}/members/{UserId}", 204, method: "DELETE");
        _keycloak.GivenAdmin(MemberGroups, 200, $$"""[{"id":"{{MailboxId}}","name":"support@acme.example","parentId":"{{ParentId}}"}]""");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityMailboxes>().AddMemberAsync(OrgId, MailboxId, UserId, Ct)).IsSuccess.ShouldBeTrue();
        (await host.Scoped<IEntityMailboxes>().RemoveMemberAsync(OrgId, MailboxId, UserId, Ct)).IsSuccess.ShouldBeTrue();
        (await host.Scoped<IEntityMailboxes>().RemoveMemberAsync(OrgId, "m-other", UserId, Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.Admin("PUT", $"{MailboxPath}/members/{UserId}").Count.ShouldBe(1);
        _keycloak.Admin("DELETE", $"{MailboxPath}/members/{UserId}").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Members_are_full_identities()
    {
        _keycloak.GivenAdmin($"{MailboxPath}/members", 200, """[{"id":"u-carol","username":"carol","email":"carol@acme.example","enabled":false}]""");
        await using AdapterHost host = Host();

        IdentityAccount carol = (await host.Scoped<IEntityMailboxes>().ListMembersAsync(OrgId, MailboxId, Ct)).Value.Single();

        carol.Enabled.ShouldBeFalse();
        carol.Email.ShouldBe("carol@acme.example");
    }

    [Fact]
    public async Task A_user_s_shared_mailboxes_are_the_subgroups_of_mailboxes_they_are_in()
    {
        GivenParent();
        _keycloak.GivenAdmin(MemberGroups, 200, $$"""[{"id":"g-fin","name":"finance","parentId":"{{GroupsParentId}}"},{"id":"{{MailboxId}}","name":"support@acme.example","parentId":"{{ParentId}}"}]""");
        GivenSupport();
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityMailboxes>().ListUserMailboxesAsync(OrgId, UserId, Ct)).Value.ShouldHaveSingleItem().Id.ShouldBe(MailboxId);
    }

    [Fact]
    public void Attributes_round_trip()
    {
        var mailbox = new SharedMailbox("m1", OrgId, Address("sales@acme.example"), "Sales") { Aliases = [Address("orders@acme.example")] };

        SharedMailbox read = KeycloakEntityMailboxes.ToMailbox(OrgId, new KeycloakGroup { Id = "m1", Name = "sales@acme.example", Attributes = KeycloakEntityMailboxes.Attributes(mailbox) })!;

        read.Name.ShouldBe("Sales");
        read.Address.ShouldBe(mailbox.Address);
        read.Aliases.ShouldBe(mailbox.Aliases);
    }

    private static JsonObject Body(WireMock.RequestMessage request) => JsonNode.Parse(request.Body!)!.AsObject();

    private static MailAddress Address(string value) => MailAddress.TryParse(value, out MailAddress? address) ? address : throw new ArgumentException(value);
}
