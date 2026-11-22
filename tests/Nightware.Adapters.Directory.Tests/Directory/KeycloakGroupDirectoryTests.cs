using Nightware.Adapters.Directory.Directory;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Common.Query;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.Directory;

/// <summary>Phase 21 groups, read from the organization groups the console keeps (shapes as in <c>KeycloakDirectoryAddressesTests</c>).</summary>
public sealed class KeycloakGroupDirectoryTests : IDisposable
{
    private const string UserId = "5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11";
    private const string OtherUserId = "7f6e5d4c-3b2a-4190-8f7e-6d5c4b3a2918";
    private const string OrgId = "org-1";
    private const string Org = $"/organizations/{OrgId}";
    private const string Finance = "11111111-1111-4111-8111-111111111111";
    private const string Board = "22222222-2222-4222-8222-222222222222";
    private const string Stray = "33333333-3333-4333-8333-333333333333";
    private const string Admins = "44444444-4444-4444-8444-444444444444";

    private readonly KeycloakStub _keycloak = new();

    public KeycloakGroupDirectoryTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdapterHost Host() => AdapterHost.Create(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = "0" });

    private void GivenMemberOf(string organizations = $$"""[{"id":"{{OrgId}}","name":"Acme","alias":"acme"}]""") =>
        _keycloak.GivenAdmin($"/organizations/members/{UserId}/organizations", 200, organizations);

    private void GivenParent(bool exists = true) =>
        _keycloak.GivenAdmin(
            $"{Org}/groups",
            200,
            exists ? $$"""[{"id":"{{Admins}}","name":"admins"},{"id":"g-parent","name":"groups"},{"id":"m-parent","name":"mailboxes"}]""" : "[]");

    private void GivenChildren(params string[] ids) =>
        _keycloak.GivenAdmin($"{Org}/groups/g-parent/children", 200, "[" + string.Join(',', ids.Select(static id => $$"""{"id":"{{id}}","name":"x"}""")) + "]");

    private void GivenGroup(string id, string name, string attributes, string parentId = "g-parent") =>
        _keycloak.GivenAdmin($"{Org}/groups/{id}", 200, $$$"""{"id":"{{{id}}}","name":"{{{name}}}","parentId":"{{{parentId}}}","attributes":{{{{attributes}}}}}""");

    private void GivenStandardGroups()
    {
        GivenGroup(Finance, "finance", """ "nightware_name":["Finance"],"nightware_list_address":["finance@acme.example"],"nightware_list_aliases":["accounts@acme.example"],"nightware_files":["true"] """);
        GivenGroup(Board, "board", """ "nightware_name":["Board papers"],"nightware_files":["true"] """);
        GivenGroup(Stray, "Not A Key!", """ "nightware_name":["Hand made"] """);
    }

    [Fact]
    public async Task Is_registered_as_the_port()
    {
        await using AdapterHost host = Host();

        host.Scoped<IGroupDirectory>().ShouldBeOfType<KeycloakGroupDirectory>();
    }

    [Fact]
    public async Task Lists_the_groups_of_the_callers_organizations_as_Graph_groups()
    {
        GivenMemberOf();
        GivenParent();
        GivenChildren(Finance, Board, Stray);
        GivenStandardGroups();
        await using AdapterHost host = Host();

        var page = (await host.Scoped<IGroupDirectory>().ListAsync(new ItemQuery(), Ct)).Value;

        page.Items.Select(static g => g.Id).ShouldBe([Finance, Board], "a subgroup the console did not make is skipped");
        var finance = page.Items[0];
        (finance.DisplayName, finance.MailNickname, finance.Mail, finance.SecurityEnabled).ShouldBe(("Finance", "finance", "finance@acme.example", true));
        finance.Aliases.ShouldBe(["accounts@acme.example"]);
        page.Items[1].MailEnabled.ShouldBeFalse();
        page.SkipToken.ShouldBeNull();
    }

    [Fact]
    public async Task Pages_by_offset_and_reads_only_the_page_s_groups_whole()
    {
        GivenMemberOf();
        GivenParent();
        GivenChildren(Finance, Board, Stray);
        GivenStandardGroups();
        await using AdapterHost host = Host();

        var first = (await host.Scoped<IGroupDirectory>().ListAsync(new ItemQuery { Top = 1 }, Ct)).Value;
        var second = (await host.Scoped<IGroupDirectory>().ListAsync(new ItemQuery { Top = 1, SkipToken = first.SkipToken }, Ct)).Value;

        first.Items.Single().Id.ShouldBe(Finance);
        first.SkipToken.ShouldBe("1");
        second.Items.Single().Id.ShouldBe(Board);
        second.SkipToken.ShouldBe("2");
        _keycloak.Admin("GET", $"{Org}/groups/{Stray}").ShouldBeEmpty();
    }

    [Fact]
    public async Task An_organization_without_a_groups_group_has_no_groups()
    {
        GivenMemberOf();
        GivenParent(exists: false);
        await using AdapterHost host = Host();

        (await host.Scoped<IGroupDirectory>().ListAsync(new ItemQuery(), Ct)).Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unsupported_options_are_refused_before_Keycloak_is_asked()
    {
        await using AdapterHost host = Host();

        var result = await host.Scoped<IGroupDirectory>().ListAsync(new ItemQuery { Search = new SearchQuery([]) }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error!.Target.ShouldBe("$search");
        _keycloak.AdminRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Gets_one_group_of_the_callers_organizations()
    {
        GivenMemberOf();
        GivenParent();
        GivenStandardGroups();
        await using AdapterHost host = Host();

        var group = (await host.Scoped<IGroupDirectory>().GetAsync(Finance, Ct)).Value;

        group.DisplayName.ShouldBe("Finance");
        group.MailEnabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData(Admins, "the admins group is no group")]
    [InlineData("not-a-guid", "a malformed id names nothing")]
    [InlineData("99999999-9999-4999-8999-999999999999", "Keycloak doesn't know it")]
    public async Task Anything_but_a_subgroup_of_groups_is_not_found(string id, string because)
    {
        GivenMemberOf();
        GivenParent();
        GivenGroup(Admins, "admins", """ "nightware_name":["Admins"] """, parentId: "org-backing");
        _keycloak.GivenAdmin($"{Org}/groups/99999999-9999-4999-8999-999999999999", 404, """{"error":"Could not find group by id"}""");
        await using AdapterHost host = Host();

        (await host.Scoped<IGroupDirectory>().GetAsync(id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound, because);
    }

    [Fact]
    public async Task A_group_in_an_organization_the_caller_is_not_in_is_not_found()
    {
        GivenMemberOf("[]");
        await using AdapterHost host = Host();

        (await host.Scoped<IGroupDirectory>().GetAsync(Finance, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _keycloak.Admin("GET", $"{Org}/groups/{Finance}").ShouldBeEmpty();
    }

    [Fact]
    public async Task Members_are_the_enabled_people_of_the_group()
    {
        GivenMemberOf();
        GivenParent();
        GivenStandardGroups();
        _keycloak.GivenAdmin(
            $"{Org}/groups/{Finance}/members",
            200,
            """
            [{"id":"u-1","username":"sara","email":"sara@acme.example","firstName":"Sara","lastName":"Davis","enabled":true},
             {"id":"u-2","username":"gone","email":"gone@acme.example","enabled":false},
             {"id":"u-3","username":"service-account-x","enabled":true}]
            """);
        await using AdapterHost host = Host();

        var page = (await host.Scoped<IGroupDirectory>().ListMembersAsync(Finance, new ItemQuery(), Ct)).Value;

        page.Items.Select(static u => u.DisplayName).ShouldBe(["Sara Davis"]);
    }

    [Fact]
    public async Task Member_of_lists_the_users_groups_in_the_callers_organizations()
    {
        GivenMemberOf();
        GivenParent();
        GivenStandardGroups();
        _keycloak.GivenAdmin(
            $"{Org}/members/{OtherUserId}/groups",
            200,
            $$"""[{"id":"{{Board}}","name":"board","parentId":"g-parent"},{"id":"{{Admins}}","name":"admins","parentId":"org-backing"}]""");
        await using AdapterHost host = Host();

        var page = (await host.Scoped<IGroupDirectory>().ListMemberOfAsync(OtherUserId, new ItemQuery(), Ct)).Value;

        page.Items.Select(static g => g.Id).ShouldBe([Board], "admins is no group");
    }

    [Fact]
    public async Task A_user_outside_the_callers_organizations_is_in_none_of_its_groups()
    {
        GivenMemberOf();
        GivenParent();
        _keycloak.GivenAdmin($"{Org}/members/{OtherUserId}/groups", 404, """{"error":"not a member"}""");
        await using AdapterHost host = Host();

        (await host.Scoped<IGroupDirectory>().ListMemberOfAsync(OtherUserId, new ItemQuery(), Ct)).Value.Items.ShouldBeEmpty();
    }
}
