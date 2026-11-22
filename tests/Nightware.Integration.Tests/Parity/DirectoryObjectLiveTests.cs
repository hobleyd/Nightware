using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Directory;
using Nightware.Adapters.Directory.Administration;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Parity;

/// <summary>
/// Phase 21 against the dev stack: the console's record (a group in bob's organization, alice as bob's manager, written
/// as the console writes them) read back through Graph's directory routes.
/// </summary>
public sealed class DirectoryObjectLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Groups_managers_and_the_organization_read_back_what_the_console_recorded()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var dev = DevStack.Open();
        await using var console = Console(dev);
        var identities = console.GetRequiredService<IIdentityAccounts>();
        var groups = console.GetRequiredService<IEntityGroups>();
        using var bob = await stack.GraphClientAsync("bob", Ct);
        using var alice = await stack.GraphClientAsync("alice", Ct);

        var organizations = JsonNode.Parse(await bob.GetStringAsync("/v1.0/organization", Ct))!["value"]!.AsArray();
        var organization = organizations.Single()!;
        organization["verifiedDomains"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()).ShouldContain(DevStack.MailDomain);
        var organizationId = organization["id"]!.GetValue<string>();
        (await bob.GetAsync($"/v1.0/organization/{organizationId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var bobId = (await identities.FindByUsernameAsync("bob", Ct)).Value.Id;
        var aliceId = (await identities.FindByUsernameAsync("alice", Ct)).Value.Id;
        GroupKey.TryParse("nw-live-" + Guid.NewGuid().ToString("N")[..8], out var key).ShouldBeTrue();
        var group = (await groups.CreateAsync(organizationId, new NewGroup(key!, "Live directory test", null, new GroupFiles(false)), Ct)).Value;
        try
        {
            (await groups.AddMemberAsync(organizationId, group.Id, bobId, Ct)).IsSuccess.ShouldBeTrue();
            (await identities.SetManagerAsync(bobId, aliceId, Ct)).IsSuccess.ShouldBeTrue();

            var manager = JsonNode.Parse(await bob.GetStringAsync("/v1.0/me/manager", Ct))!;
            manager["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.user");
            manager["id"]!.GetValue<string>().ShouldBe(aliceId);
            var reports = JsonNode.Parse(await alice.GetStringAsync("/v1.0/me/directReports", Ct))!["value"]!.AsArray();
            reports.Select(r => r!["id"]!.GetValue<string>()).ShouldContain(bobId);
            JsonNode.Parse(await alice.GetStringAsync($"/v1.0/users/{DevStack.Address("bob")}/manager?$select=id", Ct))!["id"]!.GetValue<string>().ShouldBe(aliceId);

            var memberOf = JsonNode.Parse(await bob.GetStringAsync("/v1.0/me/memberOf", Ct))!["value"]!.AsArray();
            var mine = memberOf.Single(g => g!["id"]!.GetValue<string>() == group.Id)!;
            mine["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.group");
            (mine["securityEnabled"]!.GetValue<bool>(), mine["mailEnabled"]!.GetValue<bool>(), mine["mailNickname"]!.GetValue<string>()).ShouldBe((true, false, key!.Value));
            JsonNode.Parse(await alice.GetStringAsync($"/v1.0/users/{DevStack.Address("bob")}/transitiveMemberOf", Ct))!["value"]!.AsArray()
                .Select(g => g!["id"]!.GetValue<string>()).ShouldContain(group.Id);

            var all = JsonNode.Parse(await alice.GetStringAsync("/v1.0/groups", Ct))!["value"]!.AsArray();
            all.Select(g => g!["id"]!.GetValue<string>()).ShouldContain(group.Id);
            JsonNode.Parse(await alice.GetStringAsync($"/v1.0/groups/{group.Id}", Ct))!["displayName"]!.GetValue<string>().ShouldBe("Live directory test");
            var members = JsonNode.Parse(await alice.GetStringAsync($"/v1.0/groups/{group.Id}/members?$select=id,mail", Ct))!["value"]!.AsArray();
            members.Select(m => m!["id"]!.GetValue<string>()).ShouldBe([bobId]);

            (await alice.GetAsync("/v1.0/groups/00000000-0000-4000-8000-000000000000", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await identities.SetManagerAsync(bobId, null, Ct);
            await groups.DeleteAsync(organizationId, group.Id, Ct);
        }

        var none = await bob.GetAsync("/v1.0/me/manager", Ct);
        none.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonNode.Parse(await none.Content.ReadAsStringAsync(Ct))!["error"]!["code"]!.GetValue<string>().ShouldBe("Request_ResourceNotFound");
    }

    /// <summary>The console's Keycloak adapters, as the console's own client (<c>nightware-admin</c>).</summary>
    private static ServiceProvider Console(DevStack dev)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Directory:KeycloakBaseUrl"] = DevStack.AuthBase,
            ["Directory:Realm"] = "nightware",
            ["Directory:ClientId"] = "nightware-admin",
            ["Directory:ClientSecret"] = dev.Env("NIGHTWARE_ADMIN_CLIENT_SECRET"),
            ["Directory:ServiceAccountScope"] = "kc-admin-api",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddKeycloakCore(configuration).AddKeycloakAdministration();
        dev.Trust(services);
        return services.BuildServiceProvider();
    }
}
