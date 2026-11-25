using System.Text.Json.Nodes;

using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Administration.Entities;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Administration;

public sealed class StalwartMtaStsPoliciesTests : IAsyncDisposable
{
    private readonly StalwartManagementTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMtaStsPolicies Policies => _host.Get<IMtaStsPolicies>();

    /// <summary>As Stalwart v1.1.0 answers both reads on a fresh install (mailExchangers a list keyed by index, hostname null).</summary>
    private const string Recorded = """
        [["x:MtaSts/get",{"accountId":"d333333","list":[{"maxAge":604800000,"mode":"testing","mxHosts":{},"id":"singleton"}],"notFound":[]},"c0"],
         ["x:SystemSettings/get",{"accountId":"d333333","list":[{"defaultHostname":"mail.nightware.localhost","mailExchangers":{"0":{"hostname":null,"priority":10}},"id":"singleton"}],"notFound":[]},"c1"]]
        """;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task An_empty_host_set_falls_back_on_the_mail_exchangers_and_a_null_one_is_the_default_hostname()
    {
        _host.Stub("x:MtaSts/get", Recorded);

        MtaStsPolicy policy = (await Policies.GetAsync(Ct)).Value;

        policy.Mode.ShouldBe("testing");
        policy.MxHosts.ShouldBeEmpty();
        policy.DefaultHosts.ShouldBe(["mail.nightware.localhost"]);
        policy.Hosts.ShouldBe(["mail.nightware.localhost"]);
        JsonArray get = _host.Calls("x:MtaSts/get").ShouldHaveSingleItem();
        get[1]!["ids"]!.AsArray().Select(i => i!.GetValue<string>()).ShouldBe(["singleton"]);
    }

    [Fact]
    public async Task Hosts_of_its_own_and_named_exchangers_are_read_as_sets_and_lists()
    {
        _host.Stub("x:MtaSts/get", """
            [["x:MtaSts/get",{"list":[{"mode":"enforce","mxHosts":{"mail.acme.example":true,"old.acme.example":false},"id":"singleton"}],"notFound":[]},"c0"],
             ["x:SystemSettings/get",{"list":[{"defaultHostname":"mail.server.example","mailExchangers":{"0":{"hostname":"mx1.server.example","priority":10},"1":{"hostname":null,"priority":20}},"id":"singleton"}],"notFound":[]},"c1"]]
            """);

        MtaStsPolicy policy = (await Policies.GetAsync(Ct)).Value;

        policy.MxHosts.ShouldBe(["mail.acme.example"]);
        policy.DefaultHosts.ShouldBe(["mx1.server.example", "mail.server.example"]);
    }

    [Fact]
    public async Task Setting_replaces_the_host_set_and_reloads_in_the_same_request()
    {
        _host.Stub("x:MtaSts/set", """
            [["x:MtaSts/set",{"updated":{"singleton":null}},"c0"],["x:Action/set",{"created":{"r":{"id":"a1"}}},"c1"]]
            """);

        (await Policies.SetMxHostsAsync(["mail.nightware.localhost", "mail.acme.example"], Ct)).IsSuccess.ShouldBeTrue();

        JsonArray set = _host.Calls("x:MtaSts/set").ShouldHaveSingleItem();
        set[1]!["update"]!["singleton"]!["mxHosts"]!.ToJsonString().ShouldBe("""{"mail.nightware.localhost":true,"mail.acme.example":true}""");
        _host.Calls("x:Action/set").ShouldHaveSingleItem()[1]!["create"]!["r"]!["@type"]!.GetValue<string>().ShouldBe("ReloadSettings");
    }

    [Fact]
    public async Task A_refused_update_is_an_error_and_a_failed_reload_is_not()
    {
        _host.Stub("x:MtaSts/set", """
            [["x:MtaSts/set",{"notUpdated":{"singleton":{"type":"forbidden","description":"sysMtaStsUpdate"}}},"c0"],["x:Action/set",{"created":{"r":{"id":"a1"}}},"c1"]]
            """);
        (await Policies.SetMxHostsAsync(["mail.acme.example"], Ct)).IsSuccess.ShouldBeFalse();

        await using var reloadFails = new StalwartManagementTestHost();
        reloadFails.Stub("x:MtaSts/set", """
            [["x:MtaSts/set",{"updated":{"singleton":null}},"c0"],["error",{"type":"forbidden"},"c1"]]
            """);
        (await reloadFails.Get<IMtaStsPolicies>().SetMxHostsAsync(["mail.acme.example"], Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_policy_that_cannot_be_read_is_an_error()
    {
        _host.Stub("x:MtaSts/get", """
            [["error",{"type":"forbidden","description":"sysMtaStsGet"},"c0"],["x:SystemSettings/get",{"list":[],"notFound":[]},"c1"]]
            """);

        (await Policies.GetAsync(Ct)).IsSuccess.ShouldBeFalse();
    }
}
