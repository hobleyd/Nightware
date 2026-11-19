using System.Text.Json.Nodes;

using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Tests.Administration;

public sealed class GandiDnsHostingTests : IDisposable
{
    private const string TokenValue = "a5f72d8e2b391ca6d7104e8b35f9a01c3d4762f9";
    private static readonly DnsZone Zone = new("acme.example", "acme.example");
    private static readonly DnsProviderToken Token = DnsHostsTestHost.Token(DnsProvider.Gandi, TokenValue);
    private readonly DnsHostsTestHost _host = new();

    private static CancellationToken Ct => DnsHostsTestHost.Ct;

    private IDnsHosting Hosting() => _host.Hosting(DnsProvider.Gandi);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Finds_the_livedns_domain_walking_up_past_one_the_token_may_not_see()
    {
        _host.Stub("GET", "/gandi/domains/mail.acme.example", """{"code":403,"message":"Access was denied to this resource.","object":"HTTPForbidden","cause":"Forbidden"}""", 403);
        _host.Stub("GET", "/gandi/domains/acme.example", """{"fqdn":"acme.example","automatic_snapshots":true}""");

        (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("mail.acme.example"), Ct)).Value.ShouldBe(Zone);
        _host.Requests("GET", "/gandi/domains/acme.example").Single().RequestMessage!.Headers!["Authorization"].ShouldBe([$"Bearer {TokenValue}"]);
    }

    [Fact]
    public async Task A_token_refused_everywhere_is_a_validation_error_on_the_token()
    {
        _host.Stub("GET", "/gandi/domains/acme.example", """{"code":403,"message":"Access was denied to this resource.","cause":"Forbidden"}""", 403);

        Error error = (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("acme.example"), Ct)).Error!;

        error.Target.ShouldBe("dnsToken");
        error.Message.ShouldContain("Access was denied");
    }

    [Fact]
    public async Task Lists_each_value_and_a_zone_already_holding_the_records_needs_no_change()
    {
        _host.Stub("GET", "/gandi/domains/acme.example/records", """
            [{"rrset_name":"@","rrset_type":"MX","rrset_ttl":10800,"rrset_values":["10 mail.acme.example."]},
             {"rrset_name":"@","rrset_type":"TXT","rrset_ttl":10800,"rrset_values":["\"v=spf1 mx -all\""]},
             {"rrset_name":"autoconfig","rrset_type":"CNAME","rrset_ttl":10800,"rrset_values":["mail.acme.example."]}]
            """, headers: new Dictionary<string, string> { ["Total-Count"] = "3" });

        IReadOnlyList<ZoneRecord> records = (await Hosting().ListRecordsAsync(Token, Zone, Ct)).Value;

        records.Select(static r => r.Record.Name).ShouldBe(["acme.example", "acme.example", "autoconfig.acme.example"]);
        DnsPublicationPlanner.Plan(Zone,
        [
            new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"),
            new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all"),
            new DnsRecord("autoconfig.acme.example", "CNAME", 3600, "mail.acme.example"),
        ], records).HasWork.ShouldBeFalse();
        _host.Requests("GET", "/gandi/domains/acme.example/records").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_new_value_joins_its_set_keeping_the_others_and_the_sets_ttl()
    {
        _host.Stub("GET", "/gandi/domains/acme.example/records/@/MX", """{"rrset_name":"@","rrset_type":"MX","rrset_ttl":1800,"rrset_values":["20 backup.acme.example."]}""");
        _host.Stub("GET", "/gandi/domains/acme.example/records/%40/MX", """{"rrset_name":"@","rrset_type":"MX","rrset_ttl":1800,"rrset_values":["20 backup.acme.example."]}""");
        _host.Stub("PUT", "/gandi/domains/acme.example/records/@/MX", """{"message":"DNS Record Created"}""", 201);
        _host.Stub("PUT", "/gandi/domains/acme.example/records/%40/MX", """{"message":"DNS Record Created"}""", 201);

        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Create, new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"), null), Ct)).IsSuccess.ShouldBeTrue();

        DnsHostsTestHost.ShouldBeJson(_host.Requests("PUT").Single().RequestMessage!.Body!, """{"rrset_values":["20 backup.acme.example.","10 mail.acme.example."],"rrset_ttl":1800}""");
    }

    [Fact]
    public async Task A_set_is_created_with_the_records_ttl_and_deleted_when_its_last_value_goes()
    {
        _host.Stub("GET", "/gandi/domains/acme.example/records/_dmarc/TXT", """{"code":404,"message":"Unknown record","cause":"Not Found"}""", 404);
        _host.Stub("PUT", "/gandi/domains/acme.example/records/_dmarc/TXT", "{}", 201);
        _host.Stub("GET", "/gandi/domains/acme.example/records/old/CNAME", """{"rrset_name":"old","rrset_type":"CNAME","rrset_ttl":300,"rrset_values":["elsewhere.example."]}""");
        _host.Stub("DELETE", "/gandi/domains/acme.example/records/old/CNAME", string.Empty, 204);

        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Create, new DnsRecord("_dmarc.acme.example", "TXT", 3600, "v=DMARC1; p=reject"), null), Ct)).IsSuccess.ShouldBeTrue();
        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Remove, null, new ZoneRecord("x", new DnsRecord("old.acme.example", "CNAME", 300, "elsewhere.example."))), Ct)).IsSuccess.ShouldBeTrue();

        DnsHostsTestHost.ShouldBeJson(_host.Requests("PUT").Single().RequestMessage!.Body!, """{"rrset_values":["\"v=DMARC1; p=reject\""],"rrset_ttl":3600}""");
        _host.Requests("DELETE", "/gandi/domains/acme.example/records/old/CNAME").Count.ShouldBe(1);
    }
}
