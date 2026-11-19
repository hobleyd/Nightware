using System.Xml.Linq;

using Nightware.Adapters.DnsHosts.Administration;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Tests.Administration;

public sealed class Route53DnsHostingTests : IDisposable
{
    private const string KeyId = "AKIAIOSFODNN7EXAMPLE";
    private const string Secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private static readonly DnsZone Zone = new("Z123ABC", "acme.example");
    private static readonly DnsProviderToken Token = DnsHostsTestHost.Token(DnsProvider.Route53, $"{KeyId}:{Secret}");
    private static readonly XNamespace Ns = "https://route53.amazonaws.com/doc/2013-04-01/";
    private readonly DnsHostsTestHost _host = new();

    private static CancellationToken Ct => DnsHostsTestHost.Ct;

    private IDnsHosting Hosting() => _host.Hosting(DnsProvider.Route53);

    public void Dispose() => _host.Dispose();

    private static string Xml(string root, string inner) => $"""<?xml version="1.0"?><{root} xmlns="https://route53.amazonaws.com/doc/2013-04-01/">{inner}</{root}>""";

    private static string Zones(string name, bool privateZone = false) => Xml("ListHostedZonesByNameResponse",
        $"<HostedZones><HostedZone><Id>/hostedzone/Z123ABC</Id><Name>{name}</Name><Config><PrivateZone>{(privateZone ? "true" : "false")}</PrivateZone></Config></HostedZone></HostedZones><IsTruncated>false</IsTruncated><MaxItems>1</MaxItems>");

    private static string Set(string name, string type, int ttl, params string[] values) =>
        $"<ResourceRecordSet><Name>{name}</Name><Type>{type}</Type><TTL>{ttl}</TTL><ResourceRecords>{string.Concat(values.Select(static v => $"<ResourceRecord><Value>{System.Security.SecurityElement.Escape(v)}</Value></ResourceRecord>"))}</ResourceRecords></ResourceRecordSet>";

    private static string Sets(string sets, string? next = null) => Xml("ListResourceRecordSetsResponse",
        $"<ResourceRecordSets>{sets}</ResourceRecordSets><IsTruncated>{(next is null ? "false" : "true")}</IsTruncated>{next}<MaxItems>300</MaxItems>");

    [Fact]
    public async Task Finds_the_public_hosted_zone_by_exact_name_and_signs_with_the_key()
    {
        _host.Stub("GET", "/r53/hostedzonesbyname", Zones("zz.example."), query: new Dictionary<string, string> { ["dnsname"] = "mail.acme.example" }, contentType: "text/xml");
        _host.Stub("GET", "/r53/hostedzonesbyname", Zones("acme.example."), query: new Dictionary<string, string> { ["dnsname"] = "acme.example" }, contentType: "text/xml");

        (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("mail.acme.example"), Ct)).Value.ShouldBe(Zone);

        var request = _host.Requests("GET", "/r53/hostedzonesbyname")[0].RequestMessage!;
        string authorization = request.Headers!["Authorization"].Single();
        authorization.ShouldStartWith($"AWS4-HMAC-SHA256 Credential={KeyId}/20261119/us-east-1/route53/aws4_request, SignedHeaders=host;x-amz-date, Signature=");
        authorization.ShouldNotContain(Secret);
        request.Headers["x-amz-date"].ShouldBe(["20261119T010203Z"]);
    }

    [Fact]
    public async Task A_private_zone_of_the_name_is_not_the_one()
    {
        _host.Stub("GET", "/r53/hostedzonesbyname", Zones("acme.example.", privateZone: true), contentType: "text/xml");

        (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("acme.example"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Theory]
    [InlineData(403, "InvalidClientTokenId", "dnsToken")]
    [InlineData(403, "SignatureDoesNotMatch", "dnsToken")]
    [InlineData(400, "Throttling", null)]
    public async Task Refused_keys_and_throttling_are_told_apart(int status, string code, string? target)
    {
        _host.Stub("GET", "/r53/hostedzonesbyname", Xml("ErrorResponse", $"<Error><Type>Sender</Type><Code>{code}</Code><Message>{code} happened</Message></Error><RequestId>r1</RequestId>"), status, contentType: "text/xml");

        Error error = (await Hosting().FindZoneAsync(Token, DnsHostsTestHost.Domain("acme.example"), Ct)).Error!;

        error.Target.ShouldBe(target);
        error.Kind.ShouldBe(target is null ? ErrorKind.BackendUnavailable : ErrorKind.Validation);
        _host.Logs.Lines.ShouldAllBe(l => !l.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lists_every_page_leaving_out_alias_and_routing_sets_and_a_zone_already_holding_the_records_needs_no_change()
    {
        _host.Stub("GET", "/r53/hostedzone/Z123ABC/rrset", Sets(
            Set("acme.example.", "MX", 300, "10 mail.acme.example.")
            + Set("acme.example.", "TXT", 300, "\"v=spf1 mx -all\"")
            + "<ResourceRecordSet><Name>www.acme.example.</Name><Type>A</Type><AliasTarget><HostedZoneId>Z2</HostedZoneId><DNSName>d1.cloudfront.net.</DNSName><EvaluateTargetHealth>false</EvaluateTargetHealth></AliasTarget></ResourceRecordSet>",
            "<NextRecordName>_dmarc.acme.example.</NextRecordName><NextRecordType>TXT</NextRecordType>"), priority: 20, contentType: "text/xml");
        _host.Stub("GET", "/r53/hostedzone/Z123ABC/rrset", Sets(
            Set("_dmarc.acme.example.", "TXT", 300, "\"v=DMARC1; p=reject\"")
            + Set("\\052.acme.example.", "A", 60, "81.2.69.160")), priority: 1, query: new Dictionary<string, string> { ["name"] = "_dmarc.acme.example.", ["type"] = "TXT" }, contentType: "text/xml");

        IReadOnlyList<ZoneRecord> records = (await Hosting().ListRecordsAsync(Token, Zone, Ct)).Value;

        records.Select(static r => $"{r.Record.Name} {r.Record.Type}").ShouldBe(["acme.example MX", "acme.example TXT", "_dmarc.acme.example TXT", "*.acme.example A"]);
        DnsPublicationPlanner.Plan(Zone,
        [
            new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"),
            new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all"),
            new DnsRecord("_dmarc.acme.example", "TXT", 3600, "v=DMARC1; p=reject"),
        ], records).HasWork.ShouldBeFalse();
    }

    [Fact]
    public async Task A_change_upserts_the_set_with_the_other_values_kept()
    {
        _host.Stub("GET", "/r53/hostedzone/Z123ABC/rrset", Sets(Set("acme.example.", "TXT", 300, "\"v=spf1 -all\"", "\"google-site-verification=abc\"")), contentType: "text/xml");
        _host.Stub("POST", "/r53/hostedzone/Z123ABC/rrset", Xml("ChangeResourceRecordSetsResponse", "<ChangeInfo><Id>/change/C1</Id><Status>PENDING</Status></ChangeInfo>"), contentType: "text/xml");
        var current = new ZoneRecord("x", new DnsRecord("acme.example", "TXT", 300, "\"v=spf1 -all\""));

        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Update, new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all"), current), Ct)).IsSuccess.ShouldBeTrue();

        XElement change = XDocument.Parse(_host.Requests("POST").Single().RequestMessage!.Body!).Descendants(Ns + "Change").Single();
        change.Element(Ns + "Action")!.Value.ShouldBe("UPSERT");
        XElement set = change.Element(Ns + "ResourceRecordSet")!;
        set.Element(Ns + "Name")!.Value.ShouldBe("acme.example.");
        set.Element(Ns + "TTL")!.Value.ShouldBe("300");
        set.Descendants(Ns + "Value").Select(static v => v.Value).ShouldBe(["\"google-site-verification=abc\"", "\"v=spf1 mx -all\""]);
    }

    [Fact]
    public async Task Removing_the_last_value_deletes_the_set_exactly_as_held()
    {
        _host.Stub("GET", "/r53/hostedzone/Z123ABC/rrset", Sets(Set("acme.example.", "MX", 300, "10 other.example.net.")), contentType: "text/xml");
        _host.Stub("POST", "/r53/hostedzone/Z123ABC/rrset", Xml("ChangeResourceRecordSetsResponse", "<ChangeInfo><Id>/change/C2</Id></ChangeInfo>"), contentType: "text/xml");

        (await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Remove, null, new ZoneRecord("x", new DnsRecord("acme.example", "MX", 300, "10 other.example.net."))), Ct)).IsSuccess.ShouldBeTrue();

        XElement change = XDocument.Parse(_host.Requests("POST").Single().RequestMessage!.Body!).Descendants(Ns + "Change").Single();
        change.Element(Ns + "Action")!.Value.ShouldBe("DELETE");
        change.Descendants(Ns + "Value").Single().Value.ShouldBe("10 other.example.net.");
        change.Descendants(Ns + "TTL").Single().Value.ShouldBe("300");
    }

    [Fact]
    public async Task A_refused_batch_says_why()
    {
        _host.Stub("GET", "/r53/hostedzone/Z123ABC/rrset", Sets(string.Empty), contentType: "text/xml");
        _host.Stub("POST", "/r53/hostedzone/Z123ABC/rrset", Xml("InvalidChangeBatch", "<Messages><Message>RRSet of type CNAME with DNS name x.acme.example. is not permitted</Message></Messages>"), 400, contentType: "text/xml");

        Result applied = await Hosting().ApplyAsync(Token, Zone, new DnsChange(DnsChangeKind.Create, new DnsRecord("x.acme.example", "CNAME", 3600, "mail.acme.example"), null), Ct);

        applied.Error!.Message.ShouldBe("Amazon Route 53: RRSet of type CNAME with DNS name x.acme.example. is not permitted.");
    }

    [Theory]
    [InlineData("\\052.acme.example.", "*.acme.example.")]
    [InlineData("plain.acme.example.", "plain.acme.example.")]
    [InlineData("odd\\05", "odd\\05")]
    public void Octal_escapes_in_names_are_decoded(string encoded, string decoded) =>
        Route53DnsHosting.Decode(encoded).ShouldBe(decoded);
}
