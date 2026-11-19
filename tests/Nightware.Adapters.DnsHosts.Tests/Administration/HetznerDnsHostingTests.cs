using System.Text.Json.Nodes;

using Nightware.Adapters.DnsHosts.Administration;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Tests.Administration;

public sealed class HetznerDnsHostingTests : IDisposable
{
    private const string TokenValue = "LRK9DAWQ1ZAEFSrCNEEzLCUwhYX1U3g7wMg4dTlkkDC96fyDuyJ39nVbVjCKSDfj";
    private static readonly DnsZone Zone = new("42", "acme.example");
    private static readonly DnsProviderToken Token = DnsHostsTestHost.Token(DnsProvider.Hetzner, TokenValue);
    private readonly DnsHostsTestHost _host = new();

    private static CancellationToken Ct => DnsHostsTestHost.Ct;

    private IDnsHosting Hosting() => _host.Hosting(DnsProvider.Hetzner);

    public void Dispose() => _host.Dispose();

    private static string Sets(string sets, int? nextPage) =>
        "{\"rrsets\":" + sets + ",\"meta\":{\"pagination\":{\"page\":1,\"per_page\":100,\"next_page\":"
        + (nextPage is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null") + "}}}";

    [Fact]
    public async Task Finds_the_zone_by_exact_name_walking_up_and_sends_the_token_as_bearer()
    {
        _host.Stub("GET", "/hetzner/zones", """{"zones":[],"meta":{"pagination":{"next_page":null}}}""", query: new Dictionary<string, string> { ["name"] = "mail.acme.example" });
        _host.Stub("GET", "/hetzner/zones", """{"zones":[{"id":42,"name":"acme.example","mode":"primary"}],"meta":{"pagination":{"next_page":null}}}""", query: new Dictionary<string, string> { ["name"] = "acme.example" });

        (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("mail.acme.example"), Ct)).Value.ShouldBe(Zone);
        _host.Requests("GET", "/hetzner/zones")[0].RequestMessage!.Headers!["Authorization"].ShouldBe([$"Bearer {TokenValue}"]);
    }

    [Fact]
    public async Task A_refused_token_is_a_validation_error_on_the_token()
    {
        _host.Stub("GET", "/hetzner/zones", """{"error":{"code":"unauthorized","message":"the token you have provided is invalid"}}""", 401);

        Error error = (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("acme.example"), Ct)).Error!;

        error.Target.ShouldBe("dnsToken");
        error.Message.ShouldContain("the token you have provided is invalid");
        _host.Logs.Lines.ShouldAllBe(l => !l.Contains(TokenValue, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lists_each_value_of_each_set_on_every_page_and_a_zone_already_holding_the_records_needs_no_change()
    {
        _host.Stub("GET", "/hetzner/zones/42/rrsets", Sets("""
            [{"id":"@/MX","name":"@","type":"MX","ttl":3600,"records":[{"value":"10 mail.acme.example.","comment":""},{"value":"20 old.example.net.","comment":""}]},
             {"id":"@/TXT","name":"@","type":"TXT","ttl":null,"records":[{"value":"\"v=spf1 mx -all\""}]}]
            """, 2), query: new Dictionary<string, string> { ["page"] = "1" });
        _host.Stub("GET", "/hetzner/zones/42/rrsets", Sets("""
            [{"id":"_dmarc/TXT","name":"_dmarc","type":"TXT","ttl":3600,"records":[{"value":"\"v=DMARC1; p=reject\""}]}]
            """, null), query: new Dictionary<string, string> { ["page"] = "2" });

        IReadOnlyList<ZoneRecord> records = (await Hosting().ListRecordsAsync(Token, Zone, Ct)).Value;

        records.Select(static r => r.Record).ShouldBe(
        [
            new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example."),
            new DnsRecord("acme.example", "MX", 3600, "20 old.example.net."),
            new DnsRecord("acme.example", "TXT", null, "\"v=spf1 mx -all\""),
            new DnsRecord("_dmarc.acme.example", "TXT", 3600, "\"v=DMARC1; p=reject\""),
        ]);
        records.Select(static r => r.Id).Distinct().Count().ShouldBe(4);
        (await Hosting().ListRecordsAsync(Token, Zone, Ct)).Value.Select(static r => r.Id).ShouldBe(records.Select(static r => r.Id));

        DnsPublicationPlan plan = DnsPublicationPlanner.Plan(Zone,
        [
            new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"),
            new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all"),
            new DnsRecord("_dmarc.acme.example", "TXT", 3600, "v=DMARC1; p=reject"),
        ], records);
        plan.Changes.Where(static c => c.IsWrite).ShouldHaveSingleItem().Current!.Record.Value.ShouldBe("20 old.example.net.");
    }

    [Fact]
    public async Task Adds_and_removes_one_value_with_the_sets_actions()
    {
        _host.Stub("POST", "/hetzner/zones/42/rrsets/%40/MX/actions/add_records", """{"action":{"id":1,"status":"running"}}""", 201);
        _host.Stub("POST", "/hetzner/zones/42/rrsets/%40/MX/actions/remove_records", """{"action":{"id":2,"status":"running"}}""", 201);
        _host.Stub("POST", "/hetzner/zones/42/rrsets/@/MX/actions/add_records", """{"action":{"id":1,"status":"running"}}""", 201);
        _host.Stub("POST", "/hetzner/zones/42/rrsets/@/MX/actions/remove_records", """{"action":{"id":2,"status":"running"}}""", 201);
        var old = new ZoneRecord("x", new DnsRecord("acme.example", "MX", 3600, "20 old.example.net."));

        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Create, new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"), null), Ct)).IsSuccess.ShouldBeTrue();
        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Remove, null, old), Ct)).IsSuccess.ShouldBeTrue();

        var posts = _host.Requests("POST");
        JsonNode.Parse(posts[0].RequestMessage!.Body!)!.ToJsonString().ShouldBe("""{"records":[{"value":"10 mail.acme.example.","comment":"Published by the Nightware console"}]}""");
        posts[0].RequestMessage!.Path.ShouldEndWith("/MX/actions/add_records");
        JsonNode.Parse(posts[1].RequestMessage!.Body!)!["records"]![0]!["value"]!.GetValue<string>().ShouldBe("20 old.example.net.");
        posts[1].RequestMessage!.Path.ShouldEndWith("/MX/actions/remove_records");
    }

    [Fact]
    public async Task Correcting_a_value_rewrites_the_set_keeping_the_others_and_their_comments()
    {
        string set = """{"rrset":{"name":"@","type":"TXT","ttl":3600,"records":[{"value":"\"v=spf1 -all\"","comment":""},{"value":"\"google-site-verification=abc\"","comment":"kept by hand"}]}}""";
        _host.Stub("GET", "/hetzner/zones/42/rrsets/%40/TXT", set);
        _host.Stub("GET", "/hetzner/zones/42/rrsets/@/TXT", set);
        _host.Stub("POST", "/hetzner/zones/42/rrsets/%40/TXT/actions/set_records", """{"action":{"id":3}}""", 201);
        _host.Stub("POST", "/hetzner/zones/42/rrsets/@/TXT/actions/set_records", """{"action":{"id":3}}""", 201);
        var current = new ZoneRecord("x", new DnsRecord("acme.example", "TXT", 3600, "\"v=spf1 -all\""));

        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Update, new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all"), current), Ct)).IsSuccess.ShouldBeTrue();

        DnsHostsTestHost.ShouldBeJson(_host.Requests("POST").Single().RequestMessage!.Body!,
            """{"records":[{"value":"\"google-site-verification=abc\"","comment":"kept by hand"},{"value":"\"v=spf1 mx -all\"","comment":"Published by the Nightware console"}]}""");
    }
}
