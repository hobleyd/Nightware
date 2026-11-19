using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.Directory;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.Directory;

/// <summary>
/// Group lists and shared mailboxes for the people typeahead, read from the organization groups the console keeps
/// (request shapes as in <c>KeycloakEntityGroupsTests</c> and <c>KeycloakEntityMailboxesTests</c>).
/// </summary>
public sealed class KeycloakDirectoryAddressesTests : IDisposable
{
    private const string UserId = "5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11";
    private const string OrgId = "org-1";
    private const string Org = $"/organizations/{OrgId}";

    private readonly KeycloakStub _keycloak = new();

    public KeycloakDirectoryAddressesTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdapterHost Host() => AdapterHost.Create(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = "0" });

    private void GivenMemberOf(string organizations = $$"""[{"id":"{{OrgId}}","name":"Acme","alias":"acme"}]""") =>
        _keycloak.GivenAdmin($"/organizations/members/{UserId}/organizations", 200, organizations);

    private void GivenParents(string? groups = "g-parent", string? mailboxes = "m-parent")
    {
        var top = new List<string> { """{"id":"g-admins","name":"admins"}""" };
        if (groups is not null)
        {
            top.Add($$"""{"id":"{{groups}}","name":"groups"}""");
        }

        if (mailboxes is not null)
        {
            top.Add($$"""{"id":"{{mailboxes}}","name":"mailboxes"}""");
        }

        _keycloak.GivenAdmin($"{Org}/groups", 200, "[" + string.Join(',', top) + "]");
    }

    private void GivenChildren(string parentId, params string[] ids) =>
        _keycloak.GivenAdmin($"{Org}/groups/{parentId}/children", 200, "[" + string.Join(',', ids.Select(static id => $$"""{"id":"{{id}}","name":"x"}""")) + "]");

    private void GivenGroup(string id, string name, string attributes) =>
        _keycloak.GivenAdmin($"{Org}/groups/{id}", 200, $$$"""{"id":"{{{id}}}","name":"{{{name}}}","attributes":{{{{attributes}}}}}""");

    private void GivenMyGroups(params string[] ids) =>
        _keycloak.GivenAdmin($"{Org}/members/{UserId}/groups", 200, "[" + string.Join(',', ids.Select(static id => $$"""{"id":"{{id}}","name":"x"}""")) + "]");

    [Fact]
    public async Task Is_registered_as_the_port()
    {
        await using AdapterHost host = Host();

        host.Scoped<IDirectoryAddresses>().ShouldBeOfType<KeycloakDirectoryAddresses>();
    }

    [Fact]
    public async Task Lists_the_users_group_lists_and_every_shared_mailbox_of_their_organizations()
    {
        GivenMemberOf();
        GivenParents();
        GivenChildren("g-parent", "g-household", "g-staff", "g-files", "g-board");
        GivenGroup("g-household", "household", """ "nightware_name":["Household"],"nightware_list_address":["household@acme.example"],"nightware_senders":["members"] """);
        GivenGroup("g-staff", "staff", """ "nightware_name":["All staff"],"nightware_list_address":["staff@acme.example"],"nightware_senders":["organization"] """);
        GivenGroup("g-files", "files", """ "nightware_name":["Files only"],"nightware_files":["true"] """);
        GivenGroup("g-board", "board", """ "nightware_name":["Board"],"nightware_list_address":["board@acme.example"] """);
        GivenMyGroups("g-household");
        GivenChildren("m-parent", "m-support");
        GivenGroup("m-support", "support@acme.example", """ "nightware_name":["Support Inbox"] """);
        await using AdapterHost host = Host();

        IReadOnlyList<DirectoryAddress> addresses = (await host.Scoped<IDirectoryAddresses>().ListAsync(Ct)).Value;

        addresses.ShouldBe(
        [
            new DirectoryAddress("household@acme.example", "Household"),
            new DirectoryAddress("staff@acme.example", "All staff"),
            new DirectoryAddress("support@acme.example", "Support Inbox"),
        ], "a group without a list and a members-only list the user is not in are left out");
    }

    [Fact]
    public async Task An_organization_without_groups_or_mailboxes_contributes_nothing()
    {
        GivenMemberOf();
        GivenParents(groups: null, mailboxes: null);
        await using AdapterHost host = Host();

        (await host.Scoped<IDirectoryAddresses>().ListAsync(Ct)).Value.ShouldBeEmpty();
        _keycloak.Admin("GET", $"{Org}/members/{UserId}/groups").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_user_in_no_organization_has_no_addresses()
    {
        GivenMemberOf("[]");
        await using AdapterHost host = Host();

        (await host.Scoped<IDirectoryAddresses>().ListAsync(Ct)).Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_group_deleted_mid_read_is_skipped()
    {
        GivenMemberOf();
        GivenParents(mailboxes: null);
        GivenChildren("g-parent", "g-gone");
        _keycloak.GivenAdmin($"{Org}/groups/g-gone", 404, """{"error":"Group not found"}""");
        GivenMyGroups();
        await using AdapterHost host = Host();

        (await host.Scoped<IDirectoryAddresses>().ListAsync(Ct)).Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_view_organizations_it_is_forbidden()
    {
        _keycloak.GivenAdmin($"/organizations/members/{UserId}/organizations", 403, """{"error":"HTTP 403 Forbidden"}""");
        await using AdapterHost host = Host();

        (await host.Scoped<IDirectoryAddresses>().ListAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_failing_read_fails_the_listing()
    {
        GivenMemberOf();
        GivenParents(groups: null);
        GivenChildren("m-parent", "m-support");
        _keycloak.GivenAdmin($"{Org}/groups/m-support", 503);
        await using AdapterHost host = Host();

        (await host.Scoped<IDirectoryAddresses>().ListAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Theory]
    [InlineData("members", false)]
    [InlineData("MEMBERS", false)]
    [InlineData("nonsense", false)]
    [InlineData(null, false)]
    [InlineData("organization", true)]
    [InlineData("anyone", true)]
    public void A_members_only_list_is_offered_to_its_members_only(string? senders, bool offeredToOthers)
    {
        Dictionary<string, IReadOnlyList<string>> attributes = new() { ["nightware_list_address"] = ["team@acme.example"] };
        if (senders is not null)
        {
            attributes["nightware_senders"] = [senders];
        }

        KeycloakGroup group = new() { Id = "g1", Name = "team", Attributes = attributes };

        (KeycloakDirectoryAddresses.ToListAddress(group, new HashSet<string>()) is not null).ShouldBe(offeredToOthers);
        KeycloakDirectoryAddresses.ToListAddress(group, new HashSet<string> { "g1" }).ShouldBe(new DirectoryAddress("team@acme.example", "team"), "the key names a group without a name");
    }

    [Fact]
    public void Subgroups_the_console_did_not_make_are_skipped()
    {
        Dictionary<string, IReadOnlyList<string>> list = new() { ["nightware_list_address"] = ["team@acme.example"], ["nightware_senders"] = ["anyone"] };
        KeycloakDirectoryAddresses.ToListAddress(new KeycloakGroup { Id = "g1", Name = "Made by hand", Attributes = list }, new HashSet<string>()).ShouldBeNull();
        KeycloakDirectoryAddresses.ToListAddress(new KeycloakGroup { Id = "g2", Name = "team", Attributes = new Dictionary<string, IReadOnlyList<string>> { ["nightware_list_address"] = ["not an address"], ["nightware_senders"] = ["anyone"] } }, new HashSet<string>()).ShouldBeNull();

        KeycloakDirectoryAddresses.ToMailboxAddress(new KeycloakGroup { Id = "m1", Name = "support@acme.example" }).ShouldBe(new DirectoryAddress("support@acme.example", "support@acme.example"));
        KeycloakDirectoryAddresses.ToMailboxAddress(new KeycloakGroup { Id = "m2", Name = "Support@Acme.example" }).ShouldBeNull();
        KeycloakDirectoryAddresses.ToMailboxAddress(new KeycloakGroup { Id = "m3", Name = "Made by hand" }).ShouldBeNull();
    }
}
