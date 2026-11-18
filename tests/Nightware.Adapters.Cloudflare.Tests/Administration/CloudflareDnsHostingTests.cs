using Nightware.Adapters.Cloudflare.Administration;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Cloudflare.Tests.Administration;

public sealed class CloudflareDnsHostingTests : IDisposable
{
    private static readonly DnsZone Zone = new("z1", "acme.example");
    private readonly CloudflareTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDnsHosting Hosting() => _host.Get<IDnsHosting>();

    private static MailDomain Domain(string name) => MailDomain.TryParse(name, out MailDomain? d) ? d : throw new ArgumentException(name);

    public void Dispose() => _host.Dispose();

    [Fact]
    public void Is_registered_as_the_port() => Hosting().ShouldBeOfType<CloudflareDnsHosting>();

    [Fact]
    public async Task Finds_the_zone_of_a_subdomain_by_walking_up_and_sends_the_token_as_bearer()
    {
        _host.Stub("GET", "zones", CloudflareTestHost.Success("[]"), param: "name", paramValue: "mail.acme.example");
        _host.Stub("GET", "zones", CloudflareTestHost.Success("""[{"id":"z1","name":"acme.example"}]"""), param: "name", paramValue: "acme.example");

        DnsZone zone = (await Hosting().FindZoneAsync(CloudflareTestHost.Token, Domain("mail.acme.example"), Ct)).Value;

        zone.ShouldBe(Zone);
        var calls = _host.Requests("GET", "zones");
        calls.Count.ShouldBe(2);
        calls[0].RequestMessage!.Headers!["Authorization"].ShouldBe([$"Bearer {CloudflareTestHost.TokenValue}"]);
    }

    [Fact]
    public async Task A_domain_in_no_zone_the_token_sees_is_not_found_without_asking_for_the_top_level_domain()
    {
        _host.Stub("GET", "zones", CloudflareTestHost.Success("[]"));

        (await Hosting().FindZoneAsync(CloudflareTestHost.Token, Domain("acme.example"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _host.Requests("GET", "zones").Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(401, 0)]
    [InlineData(403, 9109)]
    public async Task A_refused_token_is_a_validation_error_on_the_token_and_never_logged(int status, int code)
    {
        _host.Stub("GET", "zones", CloudflareTestHost.Failure(code, "Invalid access token"), status);

        Error error = (await Hosting().FindZoneAsync(CloudflareTestHost.Token, Domain("acme.example"), Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Target.ShouldBe("dnsToken");
        error.Message.ShouldContain("Invalid access token");
        error.Message.ShouldNotContain(CloudflareTestHost.TokenValue);
        _host.Logs.Lines.ShouldNotBeEmpty();
        _host.Logs.Lines.ShouldAllBe(l => !l.Contains(CloudflareTestHost.TokenValue));
    }

    [Fact]
    public async Task A_value_that_is_not_shaped_like_a_token_is_called_out_as_the_global_key_or_an_id()
    {
        // What Cloudflare actually answers for a bearer of 37 or 32 hex characters (Global API Key, token/zone/account id).
        _host.Stub("GET", "zones", """
            {"success":false,"errors":[{"code":6003,"message":"Invalid request headers","error_chain":[{"code":6111,"message":"Invalid format for Authorization header"}]}],"messages":[],"result":null}
            """, 400);

        Error error = (await Hosting().FindZoneAsync(CloudflareTestHost.Token, Domain("acme.example"), Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Target.ShouldBe("dnsToken");
        error.Message.ShouldContain("Global API Key");
        error.Message.ShouldContain("API Tokens");
        error.Message.ShouldNotContain(CloudflareTestHost.TokenValue);
    }

    [Fact]
    public async Task Throttling_and_outages_are_backend_unavailable()
    {
        _host.Stub("GET", "zones", CloudflareTestHost.Failure(971, "Please wait"), 429);
        (await Hosting().FindZoneAsync(CloudflareTestHost.Token, Domain("acme.example"), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);

        _host.Stub("GET", "zones", "<html>bad gateway</html>", 502, priority: 1);
        (await Hosting().FindZoneAsync(CloudflareTestHost.Token, Domain("acme.example"), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Reads_every_page_of_the_zone_in_the_records_value_format()
    {
        _host.Stub("GET", "zones/z1/dns_records", CloudflareTestHost.Success("""
            [{"id":"r1","name":"acme.example","type":"MX","content":"mail.acme.example","priority":10,"ttl":1,"proxied":false},
             {"id":"r2","name":"_imaps._tcp.acme.example","type":"SRV","content":"1 993 mail.acme.example","priority":0,"ttl":3600,
              "data":{"priority":0,"weight":1,"port":993,"target":"mail.acme.example"}}]
            """, totalPages: 2), param: "page", paramValue: "1");
        _host.Stub("GET", "zones/z1/dns_records", CloudflareTestHost.Success("""
            [{"id":"r3","name":"autoconfig.acme.example","type":"CNAME","content":"mail.acme.example","proxied":true,"ttl":1},
             {"id":"r4","name":"acme.example","type":"CAA","content":"0 issue \"letsencrypt.org\"","data":{"flags":0,"tag":"issue","value":"letsencrypt.org"}},
             {"id":"r5","name":"acme.example","type":"TXT","content":"\"v=spf1 mx -all\""}]
            """, totalPages: 2), param: "page", paramValue: "2");

        IReadOnlyList<ZoneRecord> records = (await Hosting().ListRecordsAsync(CloudflareTestHost.Token, Zone, Ct)).Value;

        records.ShouldBe(
        [
            new ZoneRecord("r1", new DnsRecord("acme.example", "MX", null, "10 mail.acme.example")),
            new ZoneRecord("r2", new DnsRecord("_imaps._tcp.acme.example", "SRV", 3600, "0 1 993 mail.acme.example")),
            new ZoneRecord("r3", new DnsRecord("autoconfig.acme.example", "CNAME", null, "mail.acme.example"), Proxied: true),
            new ZoneRecord("r4", new DnsRecord("acme.example", "CAA", null, "0 issue \"letsencrypt.org\"")),
            new ZoneRecord("r5", new DnsRecord("acme.example", "TXT", null, "\"v=spf1 mx -all\"")),
        ]);
    }

    [Fact]
    public async Task A_zone_larger_than_the_bound_fails_rather_than_being_read_in_part()
    {
        using var host = new CloudflareTestHost(s => { s["Cloudflare:RecordPageSize"] = "5"; s["Cloudflare:MaxRecords"] = "5"; });
        string six = "[" + string.Join(',', Enumerable.Range(1, 6).Select(i => $$"""{"id":"r{{i}}","name":"acme.example","type":"A","content":"192.0.2.{{i}}"}""")) + "]";
        host.Stub("GET", "zones/z1/dns_records", CloudflareTestHost.Success(six, totalPages: 3));

        (await host.Get<IDnsHosting>().ListRecordsAsync(CloudflareTestHost.Token, Zone, Ct)).Error!.Message.ShouldContain("more than 5 records");
    }

    [Fact]
    public async Task Creates_updates_and_removes_one_record_per_change()
    {
        _host.Stub("POST", "zones/z1/dns_records", CloudflareTestHost.Success("""{"id":"new"}"""));
        _host.Stub("PATCH", "zones/z1/dns_records/r1", CloudflareTestHost.Success("""{"id":"r1"}"""));
        _host.Stub("DELETE", "zones/z1/dns_records/r2", CloudflareTestHost.Success("""{"id":"r2"}"""));
        var mx = new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example");
        var cname = new DnsRecord("autoconfig.acme.example", "CNAME", null, "mail.acme.example");
        var current = new ZoneRecord("r1", cname with { Value = "old.acme.example" });

        (await Hosting().ApplyAsync(CloudflareTestHost.Token, Zone, new DnsChange(DnsChangeKind.Create, mx, null), Ct)).IsSuccess.ShouldBeTrue();
        (await Hosting().ApplyAsync(CloudflareTestHost.Token, Zone, new DnsChange(DnsChangeKind.Update, cname, current), Ct)).IsSuccess.ShouldBeTrue();
        (await Hosting().ApplyAsync(CloudflareTestHost.Token, Zone, new DnsChange(DnsChangeKind.Remove, null, current with { Id = "r2" }), Ct)).IsSuccess.ShouldBeTrue();

        var created = CloudflareTestHost.Body(_host.Requests("POST", "zones/z1/dns_records").ShouldHaveSingleItem());
        created["type"]!.GetValue<string>().ShouldBe("MX");
        created["content"]!.GetValue<string>().ShouldBe("mail.acme.example");
        created["priority"]!.GetValue<int>().ShouldBe(10);
        created["comment"]!.GetValue<string>().ShouldBe(CloudflareRecords.Comment);
        var patched = CloudflareTestHost.Body(_host.Requests("PATCH", "zones/z1/dns_records/r1").ShouldHaveSingleItem());
        patched["proxied"]!.GetValue<bool>().ShouldBeFalse();
        patched.ContainsKey("comment").ShouldBeFalse();
        _host.Requests("DELETE", "zones/z1/dns_records/r2").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Cloudflares_refusal_of_a_record_is_reported_with_its_message()
    {
        _host.Stub("POST", "zones/z1/dns_records", CloudflareTestHost.Failure(81053, "An A, AAAA, or CNAME record with that host already exists."), 400);

        Error error = (await Hosting().ApplyAsync(CloudflareTestHost.Token, Zone,
            new DnsChange(DnsChangeKind.Create, new DnsRecord("autoconfig.acme.example", "CNAME", null, "mail.acme.example"), null), Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Target.ShouldBeNull();
        error.Message.ShouldContain("already exists");
    }

    [Fact]
    public async Task Reads_are_retried_and_writes_never()
    {
        using var host = new CloudflareTestHost(s => s["Cloudflare:MaxRetryAttempts"] = "2");
        host.Stub("GET", "zones", CloudflareTestHost.Failure(10013, "unavailable"), 503);
        host.Stub("POST", "zones/z1/dns_records", CloudflareTestHost.Failure(10013, "unavailable"), 503);

        await host.Get<IDnsHosting>().FindZoneAsync(CloudflareTestHost.Token, Domain("acme.example"), Ct);
        await host.Get<IDnsHosting>().ApplyAsync(CloudflareTestHost.Token, Zone, new DnsChange(DnsChangeKind.Create, new DnsRecord("acme.example", "TXT", null, "v=spf1 -all"), null), Ct);

        host.Requests("GET", "zones").Count.ShouldBe(3);
        host.Requests("POST", "zones/z1/dns_records").Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_unreachable_cloudflare_is_backend_unavailable()
    {
        using var host = new CloudflareTestHost(s => s["Cloudflare:ApiBaseUrl"] = "http://127.0.0.1:1/client/v4/");

        (await host.Get<IDnsHosting>().FindZoneAsync(CloudflareTestHost.Token, Domain("acme.example"), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }
}
