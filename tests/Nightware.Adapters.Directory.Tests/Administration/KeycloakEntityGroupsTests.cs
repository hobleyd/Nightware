using System.Text.Json.Nodes;

using Nightware.Adapters.Directory.Administration;
using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.Administration;

/// <summary>
/// Groups as subgroups of the organization group <c>groups</c>, named by key, with admin-only attributes; request
/// shapes recorded from Keycloak 26.8.2 (phase 9 step 0 a).
/// </summary>
public sealed class KeycloakEntityGroupsTests : IDisposable
{
    private const string OrgId = "org-1";
    private const string ParentId = "g-parent";
    private const string GroupId = "g-fin";
    private const string UserId = "u-carol";
    private const string TopLevel = $"/organizations/{OrgId}/groups";
    private const string Children = $"/organizations/{OrgId}/groups/{ParentId}/children";
    private const string GroupPath = $"/organizations/{OrgId}/groups/{GroupId}";
    private const string MemberGroups = $"/organizations/{OrgId}/members/{UserId}/groups";
    private readonly KeycloakStub _keycloak = new();

    public KeycloakEntityGroupsTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdapterHost Host() => AdapterHost.CreateConsole(_keycloak.Url);

    private void GivenParent() =>
        _keycloak.GivenAdmin(TopLevel, 200, $$"""[{"id":"g-admins","name":"admins","parentId":"org-internal"},{"id":"{{ParentId}}","name":"groups","parentId":"org-internal"}]""");

    private const string FinanceJson = $$"""
        {"id":"{{GroupId}}","name":"finance","path":"/groups/finance","parentId":"{{ParentId}}","attributes":{
          "nightware_name":["Finance team"],
          "nightware_list_address":["finance@acme.example"],
          "nightware_list_aliases":["accounts@acme.example","not an address"],
          "nightware_external_recipients":["accountant@example.net"],
          "nightware_senders":["organization"],
          "nightware_files":["true"],
          "nightware_team_folder":["true"],
          "nightware_team_folder_id":["7"]} }
        """;

    private void GivenFinance() => _keycloak.GivenAdmin(GroupPath, 200, FinanceJson);

    [Fact]
    public async Task A_group_is_read_from_its_attributes()
    {
        GivenParent();
        GivenFinance();
        await using AdapterHost host = Host();

        Group group = (await host.Scoped<IEntityGroups>().GetAsync(OrgId, GroupId, Ct)).Value;

        group.Key.Value.ShouldBe("finance");
        group.Name.ShouldBe("Finance team");
        group.EntityId.ShouldBe(OrgId);
        group.Mail!.Address.Value.ShouldBe("finance@acme.example");
        group.Mail.Senders.ShouldBe(SenderPolicy.Organization);
        group.Mail.Aliases.ShouldHaveSingleItem().Value.ShouldBe("accounts@acme.example");
        group.Mail.ExternalRecipients.ShouldHaveSingleItem().Value.ShouldBe("accountant@example.net");
        group.Files.ShouldBe(new GroupFiles(TeamFolder: true) { TeamFolderId = 7 });
    }

    [Fact]
    public async Task A_group_without_attributes_is_named_by_its_key_and_has_no_facets()
    {
        Group group = KeycloakEntityGroups.ToGroup(OrgId, new KeycloakGroup { Id = "g1", Name = "board" })!;

        group.Name.ShouldBe("board");
        group.Mail.ShouldBeNull();
        group.Files.ShouldBeNull();
        KeycloakEntityGroups.ToGroup(OrgId, new KeycloakGroup { Id = "g2", Name = "Made by hand" }).ShouldBeNull();
    }

    [Fact]
    public async Task Admins_is_never_a_group_whatever_id_is_asked_for()
    {
        GivenParent();
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups/g-admins", 200, """{"id":"g-admins","name":"admins","parentId":"org-internal"}""");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityGroups>().GetAsync(OrgId, "g-admins", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task An_entity_without_the_parent_group_has_no_groups()
    {
        _keycloak.GivenAdmin(TopLevel, 200, """[{"id":"g-admins","name":"admins"}]""");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityGroups>().ListAsync(OrgId, Ct)).Value.ShouldBeEmpty();
        (await host.Scoped<IEntityGroups>().ListUserGroupsAsync(OrgId, UserId, Ct)).Value.ShouldBeEmpty();
        (await host.Scoped<IEntityGroups>().GetAsync(OrgId, GroupId, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Listing_reads_each_child_for_its_attributes_and_skips_strays()
    {
        GivenParent();
        _keycloak.GivenAdmin(Children, 200, $$"""[{"id":"{{GroupId}}","name":"finance"},{"id":"g-gone","name":"gone"},{"id":"g-hand","name":"By Hand"}]""");
        GivenFinance();
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups/g-gone", 404);
        _keycloak.GivenAdmin($"/organizations/{OrgId}/groups/g-hand", 200, $$"""{"id":"g-hand","name":"By Hand","parentId":"{{ParentId}}"}""");
        await using AdapterHost host = Host();

        IReadOnlyList<Group> groups = (await host.Scoped<IEntityGroups>().ListAsync(OrgId, Ct)).Value;

        groups.ShouldHaveSingleItem().Name.ShouldBe("Finance team");
        _keycloak.Admin("GET", Children).Single().Query!["briefRepresentation"].Single().ShouldBe("true");
    }

    [Fact]
    public async Task Creating_makes_the_parent_once_then_the_subgroup_then_writes_attributes()
    {
        _keycloak.GivenAdmin(TopLevel, 200, "[]");
        _keycloak.GivenAdmin(TopLevel, 201, method: "POST", location: $"https://kc/admin/realms/nightware/organizations/{OrgId}/groups/{ParentId}");
        _keycloak.GivenAdmin(Children, 201, method: "POST", location: $"https://kc/admin/realms/nightware{Children}/{GroupId}");
        _keycloak.GivenAdmin(GroupPath, 204, method: "PUT");
        await using AdapterHost host = Host();
        var mail = new GroupMail(Address("finance@acme.example"), SenderPolicy.Members) { ExternalRecipients = [Recipient("accountant@example.net")] };

        Group group = (await host.Scoped<IEntityGroups>().CreateAsync(OrgId, new NewGroup(Key("finance"), "Finance", mail, new GroupFiles(false)), Ct)).Value;

        group.Id.ShouldBe(GroupId);
        Body(_keycloak.Admin("POST", TopLevel).Single())["name"]!.GetValue<string>().ShouldBe("groups");
        Body(_keycloak.Admin("POST", Children).Single())["name"]!.GetValue<string>().ShouldBe("finance");
        JsonObject put = Body(_keycloak.Admin("PUT", GroupPath).Single());
        put["name"]!.GetValue<string>().ShouldBe("finance");
        JsonObject attributes = put["attributes"]!.AsObject();
        attributes["nightware_name"]![0]!.GetValue<string>().ShouldBe("Finance");
        attributes["nightware_list_address"]![0]!.GetValue<string>().ShouldBe("finance@acme.example");
        attributes["nightware_senders"]![0]!.GetValue<string>().ShouldBe("members");
        attributes["nightware_external_recipients"]![0]!.GetValue<string>().ShouldBe("accountant@example.net");
        attributes["nightware_files"]![0]!.GetValue<string>().ShouldBe("true");
        attributes["nightware_team_folder"]![0]!.GetValue<string>().ShouldBe("false");
        attributes.ContainsKey("nightware_team_folder_id").ShouldBeFalse();
    }

    [Fact]
    public async Task A_taken_key_is_a_conflict_on_the_key()
    {
        GivenParent();
        _keycloak.GivenAdmin(Children, 409, """{"errorMessage":"Top level group named 'finance' already exists."}""", method: "POST");
        await using AdapterHost host = Host();

        Error error = (await host.Scoped<IEntityGroups>().CreateAsync(OrgId, new NewGroup(Key("finance"), "Finance", null, null), Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Conflict);
        error.Target.ShouldBe("key");
        _keycloak.Admin("POST", TopLevel).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_checks_the_group_is_one_first()
    {
        GivenParent();
        GivenFinance();
        _keycloak.GivenAdmin(GroupPath, 204, method: "DELETE");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityGroups>().DeleteAsync(OrgId, GroupId, Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("DELETE", GroupPath).Count.ShouldBe(1);
        (await host.Scoped<IEntityGroups>().DeleteAsync(OrgId, "g-admins", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Members_are_full_identities()
    {
        _keycloak.GivenAdmin($"{GroupPath}/members", 200, """[{"id":"u-carol","username":"carol","email":"carol@acme.example","enabled":false}]""");
        await using AdapterHost host = Host();

        IdentityAccount carol = (await host.Scoped<IEntityGroups>().ListMembersAsync(OrgId, GroupId, Ct)).Value.Single();

        carol.Enabled.ShouldBeFalse();
        carol.Email.ShouldBe("carol@acme.example");
    }

    [Fact]
    public async Task Adding_a_member_is_a_put_and_removing_one_looks_first()
    {
        _keycloak.GivenAdmin($"{GroupPath}/members/{UserId}", 204, method: "PUT");
        _keycloak.GivenAdmin($"{GroupPath}/members/{UserId}", 204, method: "DELETE");
        _keycloak.GivenAdmin(MemberGroups, 200, $$"""[{"id":"{{GroupId}}","name":"finance","parentId":"{{ParentId}}"}]""");
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityGroups>().AddMemberAsync(OrgId, GroupId, UserId, Ct)).IsSuccess.ShouldBeTrue();
        (await host.Scoped<IEntityGroups>().RemoveMemberAsync(OrgId, GroupId, UserId, Ct)).IsSuccess.ShouldBeTrue();
        (await host.Scoped<IEntityGroups>().RemoveMemberAsync(OrgId, "g-other", UserId, Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.Admin("PUT", $"{GroupPath}/members/{UserId}").Count.ShouldBe(1);
        _keycloak.Admin("DELETE", $"{GroupPath}/members/{UserId}").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Removing_a_user_who_left_the_entity_is_done()
    {
        _keycloak.GivenAdmin(MemberGroups, 404);
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityGroups>().RemoveMemberAsync(OrgId, GroupId, UserId, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_user_s_groups_are_the_subgroups_of_groups_they_are_in()
    {
        GivenParent();
        _keycloak.GivenAdmin(MemberGroups, 200, $$"""[{"id":"g-admins","name":"admins","parentId":"org-internal"},{"id":"{{GroupId}}","name":"finance","parentId":"{{ParentId}}"}]""");
        GivenFinance();
        await using AdapterHost host = Host();

        (await host.Scoped<IEntityGroups>().ListUserGroupsAsync(OrgId, UserId, Ct)).Value.ShouldHaveSingleItem().Id.ShouldBe(GroupId);
    }

    [Fact]
    public async Task Attributes_round_trip()
    {
        var group = new Group("g1", OrgId, Key("board"), "Board")
        {
            Mail = new GroupMail(Address("board@acme.example"), SenderPolicy.Anyone) { Aliases = [Address("directors@acme.example")] },
            Files = new GroupFiles(TeamFolder: true) { TeamFolderId = 12 },
        };

        IReadOnlyDictionary<string, IReadOnlyList<string>> attributes = KeycloakEntityGroups.Attributes(group);

        Group read = KeycloakEntityGroups.ToGroup(OrgId, new KeycloakGroup { Id = "g1", Name = "board", Attributes = attributes })!;
        read.Name.ShouldBe("Board");
        read.Mail!.Address.ShouldBe(group.Mail.Address);
        read.Mail.Senders.ShouldBe(SenderPolicy.Anyone);
        read.Mail.Aliases.ShouldBe(group.Mail.Aliases);
        read.Mail.ExternalRecipients.ShouldBeEmpty();
        read.Files.ShouldBe(group.Files);
    }

    private static JsonObject Body(WireMock.RequestMessage request) => JsonNode.Parse(request.Body!)!.AsObject();

    private static GroupKey Key(string value) => GroupKey.TryParse(value, out GroupKey? key) ? key : throw new ArgumentException(value);

    private static MailAddress Address(string value) => MailAddress.TryParse(value, out MailAddress? address) ? address : throw new ArgumentException(value);

    private static RecipientAddress Recipient(string value) => RecipientAddress.TryParse(value, out RecipientAddress? r) ? r : throw new ArgumentException(value);
}
