using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Nightware.Adapters.DnsHosts.Administration;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Tests.Administration;

public sealed class GoogleCloudDnsHostingTests : IDisposable
{
    private const string Sets = "/gdns/projects/acme-dns/managedZones/acme-zone/rrsets";
    private static readonly DnsZone Zone = new("acme-zone", "acme.example");
    private readonly RSA _key = RSA.Create(2048);
    private readonly DnsHostsTestHost _host = new();
    private readonly DnsProviderToken _token;

    public GoogleCloudDnsHostingTests()
    {
        var json = new JsonObject
        {
            ["type"] = "service_account",
            ["project_id"] = "acme-dns",
            ["private_key_id"] = "key-1",
            ["private_key"] = _key.ExportPkcs8PrivateKeyPem(),
            ["client_email"] = "dns@acme-dns.iam.gserviceaccount.com",
        };
        _token = DnsHostsTestHost.Token(DnsProvider.GoogleCloud, json.ToJsonString());
        _host.Stub("POST", "/google/token", """{"access_token":"gdns-access-token","expires_in":3599,"token_type":"Bearer"}""");
    }

    private static CancellationToken Ct => DnsHostsTestHost.Ct;

    private IDnsHosting Hosting() => _host.Hosting(DnsProvider.GoogleCloud);

    public void Dispose()
    {
        _host.Dispose();
        _key.Dispose();
    }

    private static string Base64UrlDecode(string text) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - (text.Length % 4)) % 4)));

    [Fact]
    public async Task Signs_in_with_a_jwt_signed_by_the_key_and_finds_the_public_zone_by_name()
    {
        _host.Stub("GET", "/gdns/projects/acme-dns/managedZones", """{"managedZones":[]}""", query: new Dictionary<string, string> { ["dnsName"] = "mail.acme.example." });
        _host.Stub("GET", "/gdns/projects/acme-dns/managedZones", """
            {"managedZones":[{"name":"acme-private","dnsName":"acme.example.","visibility":"private"},{"name":"acme-zone","dnsName":"acme.example.","visibility":"public"}]}
            """, query: new Dictionary<string, string> { ["dnsName"] = "acme.example." });

        (await Hosting().FindZoneAsync(_token, DnsHostsTestHost.Domain("mail.acme.example"), Ct)).Value.ShouldBe(Zone);

        string form = _host.Requests("POST", "/google/token").ShouldHaveSingleItem().RequestMessage!.Body!;
        form.ShouldContain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer");
        string[] jwt = Uri.UnescapeDataString(form.Split("assertion=")[1]).Split('.');
        JsonNode header = JsonNode.Parse(Base64UrlDecode(jwt[0]))!;
        header["alg"]!.GetValue<string>().ShouldBe("RS256");
        header["kid"]!.GetValue<string>().ShouldBe("key-1");
        JsonNode claims = JsonNode.Parse(Base64UrlDecode(jwt[1]))!;
        claims["iss"]!.GetValue<string>().ShouldBe("dns@acme-dns.iam.gserviceaccount.com");
        claims["scope"]!.GetValue<string>().ShouldBe("https://www.googleapis.com/auth/ndev.clouddns.readwrite");
        claims["aud"]!.GetValue<string>().ShouldBe($"{_host.Server.Url}/google/token");
        (claims["exp"]!.GetValue<long>() - claims["iat"]!.GetValue<long>()).ShouldBe(3600);
        byte[] signature = Convert.FromBase64String(jwt[2].Replace('-', '+').Replace('_', '/') + new string('=', (4 - (jwt[2].Length % 4)) % 4));
        _key.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).ShouldBeTrue();
        _host.Requests("GET")[0].RequestMessage!.Headers!["Authorization"].ShouldBe(["Bearer gdns-access-token"]);
    }

    [Fact]
    public async Task The_access_token_is_reused_until_shortly_before_it_expires()
    {
        _host.Stub("GET", "/gdns/projects/acme-dns/managedZones", """{"managedZones":[{"name":"acme-zone","dnsName":"acme.example.","visibility":"public"}]}""");

        await Hosting().FindZoneAsync(_token, DnsHostsTestHost.Domain("acme.example"), Ct);
        await Hosting().FindZoneAsync(_token, DnsHostsTestHost.Domain("acme.example"), Ct);
        _host.Requests("POST", "/google/token").Count.ShouldBe(1);

        _host.Clock.Advance(TimeSpan.FromMinutes(58));
        await Hosting().FindZoneAsync(_token, DnsHostsTestHost.Domain("acme.example"), Ct);
        _host.Requests("POST", "/google/token").Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("/google/token", 400, """{"error":"invalid_grant","error_description":"Invalid JWT Signature."}""", "Invalid JWT Signature")]
    [InlineData("/gdns/projects/acme-dns/managedZones", 403, """{"error":{"code":403,"message":"Forbidden","status":"PERMISSION_DENIED","errors":[{"reason":"forbidden"}]}}""", "Forbidden")]
    public async Task A_refused_key_is_a_validation_error_on_the_token(string path, int status, string body, string said)
    {
        _host.Stub(path == "/google/token" ? "POST" : "GET", path, body, status, priority: 1);

        Error error = (await Hosting().FindZoneAsync(_token, DnsHostsTestHost.Domain("acme.example"), Ct)).Error!;

        error.Target.ShouldBe("dnsToken");
        error.Message.ShouldContain(said);
        _host.Logs.Lines.ShouldAllBe(l => !l.Contains("PRIVATE KEY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lists_every_page_leaving_out_routing_policy_sets_and_a_zone_already_holding_the_records_needs_no_change()
    {
        _host.Stub("GET", Sets, """
            {"rrsets":[{"name":"acme.example.","type":"MX","ttl":300,"rrdatas":["10 mail.acme.example."]},
                       {"name":"www.acme.example.","type":"A","ttl":300,"routingPolicy":{"geo":{"items":[]}}}],"nextPageToken":"p2"}
            """, priority: 20);
        _host.Stub("GET", Sets, """
            {"rrsets":[{"name":"acme.example.","type":"TXT","ttl":300,"rrdatas":["\"v=spf1 mx -all\""]},
                       {"name":"_25._tcp.mail.acme.example.","type":"TLSA","ttl":300,"rrdatas":["3 1 1 ABCDEF"]}]}
            """, priority: 1, query: new Dictionary<string, string> { ["pageToken"] = "p2" });

        IReadOnlyList<ZoneRecord> records = (await Hosting().ListRecordsAsync(_token, Zone, Ct)).Value;

        records.Select(static r => r.Record.Type).ShouldBe(["MX", "TXT", "TLSA"]);
        DnsPublicationPlanner.Plan(Zone,
        [
            new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"),
            new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all"),
            new DnsRecord("_25._tcp.mail.acme.example", "TLSA", 3600, "3 1 1 abcdef"),
        ], records).HasWork.ShouldBeFalse();
    }

    [Fact]
    public async Task A_new_set_is_created_an_existing_one_patched_and_an_emptied_one_deleted()
    {
        _host.Stub("GET", Sets, """{"rrsets":[]}""", query: new Dictionary<string, string> { ["type"] = "TXT" });
        _host.Stub("GET", Sets, """{"rrsets":[{"name":"acme.example.","type":"MX","ttl":300,"rrdatas":["20 backup.acme.example."]}]}""", query: new Dictionary<string, string> { ["type"] = "MX" });
        _host.Stub("GET", Sets, """{"rrsets":[{"name":"old.acme.example.","type":"CNAME","ttl":300,"rrdatas":["elsewhere.example."]}]}""", query: new Dictionary<string, string> { ["type"] = "CNAME" });
        _host.Stub("POST", Sets, "{}");
        _host.Stub("PATCH", $"{Sets}/acme.example./MX", "{}");
        _host.Stub("DELETE", $"{Sets}/old.acme.example./CNAME", "{}");

        (await Hosting().ApplyAsync(_token, Zone, new DnsChange(DnsChangeKind.Create, new DnsRecord("_dmarc.acme.example", "TXT", 3600, "v=DMARC1; p=reject"), null), Ct)).IsSuccess.ShouldBeTrue();
        (await Hosting().ApplyAsync(_token, Zone, new DnsChange(DnsChangeKind.Create, new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"), null), Ct)).IsSuccess.ShouldBeTrue();
        (await Hosting().ApplyAsync(_token, Zone, new DnsChange(DnsChangeKind.Remove, null, new ZoneRecord("x", new DnsRecord("old.acme.example", "CNAME", 300, "elsewhere.example."))), Ct)).IsSuccess.ShouldBeTrue();

        DnsHostsTestHost.ShouldBeJson(_host.Requests("POST", Sets).Single().RequestMessage!.Body!,
            """{"name":"_dmarc.acme.example.","type":"TXT","ttl":3600,"rrdatas":["\"v=DMARC1; p=reject\""]}""");
        DnsHostsTestHost.ShouldBeJson(_host.Requests("PATCH").Single().RequestMessage!.Body!,
            """{"name":"acme.example.","type":"MX","ttl":300,"rrdatas":["20 backup.acme.example.","10 mail.acme.example."]}""");
        _host.Requests("DELETE").Count.ShouldBe(1);
    }

    [Fact]
    public void A_key_that_is_not_rsa_signs_nothing()
    {
        var account = new GoogleCloudDnsHosting.ServiceAccount("p", "a@b", null, "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n");

        GoogleCloudDnsHosting.Assertion(account, new Uri("https://oauth2.googleapis.com/token"), DateTimeOffset.UnixEpoch).ShouldBeNull();
    }
}
