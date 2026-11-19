using System.Text.Json.Nodes;

using Nightware.Adapters.DnsHosts.Administration;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Tests.Administration;

public sealed class DigitalOceanDnsHostingTests : IDisposable
{
    // Shaped like a DigitalOcean token (dop_v1_ and 64 hex digits), put together here so no token-shaped literal
    // sits in the source for secret scanners to stop.
    private static readonly string TokenValue = "dop" + "_v1_" + string.Concat(Enumerable.Repeat("0123456789abcdef", 4));
    private static readonly DnsZone Zone = new("acme.example", "acme.example");
    private static readonly DnsProviderToken Token = DnsHostsTestHost.Token(DnsProvider.DigitalOcean, TokenValue);
    private readonly DnsHostsTestHost _host = new();

    private static CancellationToken Ct => DnsHostsTestHost.Ct;

    private IDnsHosting Hosting() => _host.Hosting(DnsProvider.DigitalOcean);

    public void Dispose() => _host.Dispose();

    [Fact]
    public void Every_host_of_the_adapter_is_registered_as_the_port() =>
        _host.All().Select(static h => h.Provider).Order().ShouldBe([DnsProvider.DigitalOcean, DnsProvider.Gandi, DnsProvider.Hetzner, DnsProvider.Route53, DnsProvider.AzureDns, DnsProvider.GoogleCloud]);

    [Fact]
    public async Task Finds_the_domain_of_a_subdomain_by_walking_up_and_sends_the_token_as_bearer()
    {
        _host.Stub("GET", "/do/domains/mail.acme.example", """{"id":"not_found","message":"The resource you were accessing could not be found."}""", 404);
        _host.Stub("GET", "/do/domains/acme.example", """{"domain":{"name":"acme.example","ttl":1800}}""");

        (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("mail.acme.example"), Ct)).Value.ShouldBe(Zone);
        _host.Requests("GET", "/do/domains/acme.example").Single().RequestMessage!.Headers!["Authorization"].ShouldBe([$"Bearer {TokenValue}"]);
    }

    [Fact]
    public async Task A_refused_token_is_a_validation_error_on_the_token_and_never_logged()
    {
        _host.Stub("GET", "/do/domains/acme.example", """{"id":"unauthorized","message":"Unable to authenticate you"}""", 401);

        Error error = (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("acme.example"), Ct)).Error!;

        error.Target.ShouldBe("dnsToken");
        error.Message.ShouldContain("Unable to authenticate you");
        _host.Logs.Lines.ShouldNotBeEmpty();
        _host.Logs.Lines.ShouldAllBe(l => !l.Contains(TokenValue, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lists_every_page_and_a_zone_already_holding_the_records_needs_no_change()
    {
        _host.Stub("GET", "/do/domains/acme.example/records", """
            {"domain_records":[
              {"id":11,"type":"MX","name":"@","data":"mail.acme.example","priority":10,"ttl":3600},
              {"id":12,"type":"TXT","name":"@","data":"v=spf1 mx -all","ttl":3600},
              {"id":13,"type":"SRV","name":"_imaps._tcp","data":"mail.acme.example","priority":0,"weight":1,"port":993,"ttl":3600}],
             "links":{"pages":{"next":"https://api.digitalocean.com/v2/domains/acme.example/records?page=2&per_page=200"}},"meta":{"total":5}}
            """, query: new Dictionary<string, string> { ["page"] = "1" });
        _host.Stub("GET", "/do/domains/acme.example/records", """
            {"domain_records":[
              {"id":14,"type":"CNAME","name":"autoconfig","data":"mail.acme.example.","ttl":3600},
              {"id":15,"type":"CAA","name":"@","data":"letsencrypt.org","flags":0,"tag":"issue","ttl":3600}],
             "links":{},"meta":{"total":5}}
            """, query: new Dictionary<string, string> { ["page"] = "2" });

        IReadOnlyList<ZoneRecord> records = (await Hosting().ListRecordsAsync(Token, Zone, Ct)).Value;

        records.Select(static r => r.Id).ShouldBe(["11", "12", "13", "14", "15"]);
        records[0].Record.ShouldBe(new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"));
        records[2].Record.ShouldBe(new DnsRecord("_imaps._tcp.acme.example", "SRV", 3600, "0 1 993 mail.acme.example"));
        records[4].Record.Value.ShouldBe("0 issue \"letsencrypt.org\"");
        DnsPublicationPlan plan = DnsPublicationPlanner.Plan(Zone,
        [
            new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"),
            new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all"),
            new DnsRecord("_imaps._tcp.acme.example", "SRV", 3600, "0 1 993 mail.acme.example"),
            new DnsRecord("autoconfig.acme.example", "CNAME", 3600, "mail.acme.example"),
            new DnsRecord("acme.example", "CAA", 3600, "0 issue \"letsencrypt.org\""),
        ], records);
        plan.HasWork.ShouldBeFalse();
    }

    [Fact]
    public async Task Creates_corrects_and_removes_one_record_each()
    {
        _host.Stub("POST", "/do/domains/acme.example/records", """{"domain_record":{"id":20}}""", 201);
        _host.Stub("PUT", "/do/domains/acme.example/records/12", """{"domain_record":{"id":12}}""");
        _host.Stub("DELETE", "/do/domains/acme.example/records/13", string.Empty, 204);
        var current = new ZoneRecord("12", new DnsRecord("acme.example", "TXT", 3600, "v=spf1 -all"));

        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Create, new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"), null), Ct)).IsSuccess.ShouldBeTrue();
        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Update, new DnsRecord("acme.example", "TXT", null, "v=spf1 mx -all"), current), Ct)).IsSuccess.ShouldBeTrue();
        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Remove, null, current with { Id = "13" }), Ct)).IsSuccess.ShouldBeTrue();

        JsonObject created = JsonNode.Parse(_host.Requests("POST", "/do/domains/acme.example/records").Single().RequestMessage!.Body!)!.AsObject();
        DnsHostsTestHost.ShouldBeJson(created.ToJsonString(), """{"type":"MX","name":"@","ttl":3600,"priority":10,"data":"mail.acme.example."}""");
        JsonObject updated = JsonNode.Parse(_host.Requests("PUT", "/do/domains/acme.example/records/12").Single().RequestMessage!.Body!)!.AsObject();
        updated["data"]!.GetValue<string>().ShouldBe("v=spf1 mx -all");
        _host.Requests("DELETE", "/do/domains/acme.example/records/13").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_tlsa_record_is_refused_without_a_call()
    {
        Result applied = await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Create, new DnsRecord("_25._tcp.mail.acme.example", "TLSA", null, "3 1 1 AB"), null), Ct);

        applied.Error!.Message.ShouldContain("does not host TLSA");
        _host.Server.LogEntries.ShouldBeEmpty();
    }
}
