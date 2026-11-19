using System.Text.Json.Nodes;

using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Tests.Administration;

public sealed class AzureDnsHostingTests : IDisposable
{
    private const string Tenant = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    private const string Client = "00000000-1111-2222-3333-444444444444";
    private const string Subscription = "55555555-6666-7777-8888-999999999999";
    private const string Secret = "abc~SECRET.123_value";
    private const string Zones = $"/subscriptions/{Subscription}/resourceGroups/rg-dns/providers/Microsoft.Network/dnsZones";
    private static readonly DnsZone Zone = new("acme.example", "acme.example");
    private static readonly DnsProviderToken Token = DnsHostsTestHost.Token(DnsProvider.AzureDns, $"{Tenant}:{Client}:{Subscription}:rg-dns:{Secret}");
    private readonly DnsHostsTestHost _host = new();

    public AzureDnsHostingTests() =>
        _host.Stub("POST", $"/entra/{Tenant}/oauth2/v2.0/token", """{"token_type":"Bearer","expires_in":3599,"access_token":"arm-access-token"}""");

    private static CancellationToken Ct => DnsHostsTestHost.Ct;

    private IDnsHosting Hosting() => _host.Hosting(DnsProvider.AzureDns);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Signs_in_once_with_client_credentials_and_reads_the_zone_in_the_resource_group()
    {
        _host.Stub("GET", $"{Zones}/mail.acme.example", """{"error":{"code":"ResourceNotFound","message":"The Resource 'Microsoft.Network/dnszones/mail.acme.example' was not found."}}""", 404);
        _host.Stub("GET", $"{Zones}/acme.example", """{"id":"x","name":"acme.example","type":"Microsoft.Network/dnszones","properties":{"zoneType":"Public"}}""");

        (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("mail.acme.example"), Ct)).Value.ShouldBe(Zone);

        var signIn = _host.Requests("POST", $"/entra/{Tenant}/oauth2/v2.0/token").ShouldHaveSingleItem().RequestMessage!;
        string form = signIn.Body!;
        form.ShouldContain("grant_type=client_credentials");
        form.ShouldContain($"client_id={Client}");
        form.ShouldContain("%2F.default");
        var read = _host.Requests("GET", $"{Zones}/acme.example").Single().RequestMessage!;
        read.Headers!["Authorization"].ShouldBe(["Bearer arm-access-token"]);
        read.Query!["api-version"].ShouldBe(["2018-05-01"]);
        _host.Logs.Lines.ShouldAllBe(l => !l.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_wrong_secret_is_a_validation_error_on_the_token()
    {
        _host.Stub("POST", $"/entra/{Tenant}/oauth2/v2.0/token",
            """{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided.\r\nTrace ID: 1","error_codes":[7000215]}""", 401, priority: 1);

        Error error = (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("acme.example"), Ct)).Error!;

        error.Target.ShouldBe("dnsToken");
        error.Message.ShouldBe("Azure DNS refused the credential (AADSTS7000215: Invalid client secret provided). Check that it is current and may edit the domain's zone.");
    }

    [Fact]
    public async Task A_name_outside_the_principals_role_is_not_its_zone_and_the_walk_goes_on()
    {
        const string OutOfScope = """{"error":{"code":"AuthorizationFailed","message":"The client does not have authorization to perform action over scope or the scope is invalid."}}""";
        _host.Stub("GET", $"{Zones}/mail.acme.example", OutOfScope, 403);
        _host.Stub("GET", $"{Zones}/acme.example", """{"name":"acme.example"}""");
        _host.Stub("GET", $"{Zones}/other.example", OutOfScope, 403);

        (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("mail.acme.example"), Ct)).Value.ShouldBe(Zone);
        (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("other.example"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task An_expired_access_token_on_the_zone_is_still_a_refused_credential()
    {
        _host.Stub("GET", $"{Zones}/acme.example", """{"error":{"code":"ExpiredAuthenticationToken","message":"The access token expiry has passed."}}""", 401);

        (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("acme.example"), Ct)).Error!.Target.ShouldBe("dnsToken");
    }

    [Fact]
    public async Task Lists_every_page_leaving_out_alias_sets_and_a_zone_already_holding_the_records_needs_no_change()
    {
        _host.Stub("GET", $"{Zones}/acme.example/recordsets", $$$"""
            {"value":[
              {"name":"@","type":"Microsoft.Network/dnszones/MX","etag":"e1","properties":{"TTL":3600,"MXRecords":[{"preference":10,"exchange":"mail.acme.example"}]}},
              {"name":"@","type":"Microsoft.Network/dnszones/TXT","etag":"e2","properties":{"TTL":3600,"TXTRecords":[{"value":["v=spf1 mx -all"]}]}},
              {"name":"www","type":"Microsoft.Network/dnszones/A","properties":{"TTL":60,"targetResource":{"id":"/subscriptions/x/frontdoor"},"ARecords":[]}}],
             "nextLink":"{{{_host.Server.Url}}}{{{Zones}}}/acme.example/recordsets?api-version=2018-05-01&$skipToken=p2"}
            """, priority: 20);
        _host.Stub("GET", $"{Zones}/acme.example/recordsets", """
            {"value":[
              {"name":"_imaps._tcp","type":"Microsoft.Network/dnszones/SRV","properties":{"TTL":3600,"SRVRecords":[{"priority":0,"weight":1,"port":993,"target":"mail.acme.example"}]}},
              {"name":"@","type":"Microsoft.Network/dnszones/CAA","properties":{"TTL":3600,"caaRecords":[{"flags":0,"tag":"issue","value":"letsencrypt.org"}]}},
              {"name":"autoconfig","type":"Microsoft.Network/dnszones/CNAME","properties":{"TTL":3600,"CNAMERecord":{"cname":"mail.acme.example"}}}]}
            """, priority: 1, query: new Dictionary<string, string> { ["$skipToken"] = "p2" });

        IReadOnlyList<ZoneRecord> records = (await Hosting().ListRecordsAsync(Token, Zone, Ct)).Value;

        records.Select(static r => $"{r.Record.Name} {r.Record.Type} {r.Record.Value}").ShouldBe(
        [
            "acme.example MX 10 mail.acme.example",
            "acme.example TXT v=spf1 mx -all",
            "_imaps._tcp.acme.example SRV 0 1 993 mail.acme.example",
            "acme.example CAA 0 issue \"letsencrypt.org\"",
            "autoconfig.acme.example CNAME mail.acme.example",
        ]);
        DnsPublicationPlanner.Plan(Zone, [.. records.Select(static r => r.Record)], records).HasWork.ShouldBeFalse();
    }

    [Fact]
    public async Task A_next_page_on_another_host_is_not_followed()
    {
        _host.Stub("GET", $"{Zones}/acme.example/recordsets", """{"value":[],"nextLink":"https://evil.example/steal"}""");

        (await Hosting().ListRecordsAsync(Token, Zone, Ct)).Value.ShouldBeEmpty();
        _host.Requests("GET").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_change_rewrites_the_set_keeping_the_other_entries_metadata_and_etag()
    {
        _host.Stub("GET", $"{Zones}/acme.example/TXT/@", """
            {"name":"@","etag":"etag-1","properties":{"TTL":300,"metadata":{"owner":"web"},"TXTRecords":[{"value":["v=spf1 -all"]},{"value":["google-site-verification=abc"]}]}}
            """);
        _host.Stub("PUT", $"{Zones}/acme.example/TXT/@", "{}");
        var current = new ZoneRecord("x", new DnsRecord("acme.example", "TXT", 300, "v=spf1 -all"));

        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Update, new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all"), current), Ct)).IsSuccess.ShouldBeTrue();

        var put = _host.Requests("PUT").Single().RequestMessage!;
        put.Headers!["If-Match"].ShouldBe(["etag-1"]);
        DnsHostsTestHost.ShouldBeJson(put.Body!,
            """{"properties":{"TTL":300,"metadata":{"owner":"web"},"TXTRecords":[{"value":["google-site-verification=abc"]},{"value":["v=spf1 mx -all"]}]}}""");
    }

    [Fact]
    public async Task A_new_set_is_created_with_the_records_ttl_and_a_long_txt_value_is_split()
    {
        _host.Stub("GET", $"{Zones}/acme.example/TXT/sel._domainkey", """{"error":{"code":"NotFound","message":"The resource record 'sel' does not exist."}}""", 404);
        _host.Stub("PUT", $"{Zones}/acme.example/TXT/sel._domainkey", "{}", 201);
        string dkim = "v=DKIM1; k=rsa; p=" + new string('A', 300);

        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Create, new DnsRecord("sel._domainkey.acme.example", "TXT", 3600, dkim), null), Ct)).IsSuccess.ShouldBeTrue();

        JsonObject body = JsonNode.Parse(_host.Requests("PUT").Single().RequestMessage!.Body!)!["properties"]!.AsObject();
        body["TTL"]!.GetValue<int>().ShouldBe(3600);
        string[] strings = [.. body["TXTRecords"]![0]!["value"]!.AsArray().Select(static s => s!.GetValue<string>())];
        strings.Select(static s => s.Length).ShouldBe([255, dkim.Length - 255]);
        string.Concat(strings).ShouldBe(dkim);
    }

    [Fact]
    public async Task Removing_the_last_entry_deletes_the_set_and_tlsa_is_refused_without_a_call()
    {
        _host.Stub("GET", $"{Zones}/acme.example/MX/@", """{"etag":"e9","properties":{"TTL":3600,"MXRecords":[{"preference":10,"exchange":"old.example.net"}]}}""");
        _host.Stub("DELETE", $"{Zones}/acme.example/MX/@", string.Empty);

        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Remove, null, new ZoneRecord("x", new DnsRecord("acme.example", "MX", 3600, "10 old.example.net"))), Ct)).IsSuccess.ShouldBeTrue();
        _host.Requests("DELETE").Single().RequestMessage!.Headers!["If-Match"].ShouldBe(["e9"]);

        Result tlsa = await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Create, new DnsRecord("_25._tcp.mail.acme.example", "TLSA", null, "3 1 1 AB"), null), Ct);
        tlsa.Error!.Message.ShouldContain("does not host TLSA");
    }
}
