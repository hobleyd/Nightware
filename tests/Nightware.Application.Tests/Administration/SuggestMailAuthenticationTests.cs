using System.Net;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

/// <summary>The hardened SPF and DMARC suggested to a domain with none of its own, and where its addresses come from.</summary>
public sealed class SuggestMailAuthenticationTests
{
    private const string Host = "mail.operator.example";
    private const string Token = "cf-token-0123456789abcdefghijklmnopqrstuv";

    private static readonly DnsZone OperatorZone = new("z-op", "operator.example");

    private readonly AdminTestBed _bed = new();
    private readonly IDnsHosting _hosting = Substitute.For<IDnsHosting>();
    private readonly IPublicDns _dns = Substitute.For<IPublicDns>();

    private static CancellationToken Ct => AdminTestBed.Ct;

    public SuggestMailAuthenticationTests()
    {
        _bed.Options.PublicHost = Host;
        _dns.ResolveAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<IPAddress>>.Success([IPAddress.Parse("81.2.69.160")]));
        _bed.Entities.GetSealedDnsTokenAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<string?>.Success("sealed:" + Token));
        _hosting.FindZoneAsync(default!, default!, Ct).ReturnsForAnyArgs(OperatorZone);
    }

    private SuggestMailAuthentication Suggest() => new(_bed.Actor, _bed.Entities, new PublicAddresses(new DnsTokenVault(_bed.Entities, new PrefixSealer()), _hosting, _dns, _bed.Options));

    private void WithCloudflare(params ZoneRecord[] records)
    {
        _bed.Entities.GetAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(AdminTestBed.AcmeEntity with { HasDnsToken = true });
        _hosting.ListRecordsAsync(default!, default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<ZoneRecord>>.Success(records));
    }

    [Fact]
    public async Task Without_cloudflare_the_public_hosts_public_dns_addresses_may_send()
    {
        MailAuthenticationSuggestion suggestion = (await Suggest().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        suggestion.Spf.Value.ShouldBe("v=spf1 ip4:81.2.69.160 -all");
        suggestion.Dmarc.Value.ShouldBe("v=DMARC1; p=reject; sp=reject; adkim=s; aspf=s; rua=mailto:postmaster@acme.example");
        suggestion.Source.ShouldBe(PublicAddressSource.PublicDns);
        suggestion.Host.ShouldBe(Host);
        await _dns.Received(1).ResolveAsync(Host, Arg.Any<CancellationToken>());
        await _hosting.DidNotReceiveWithAnyArgs().FindZoneAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task With_cloudflare_the_hosts_records_in_the_entitys_zone_are_used_first()
    {
        WithCloudflare(
            new ZoneRecord("r1", new DnsRecord(Host, "A", 300, "81.2.69.161")),
            new ZoneRecord("r2", new DnsRecord(Host, "AAAA", 300, "2a01:4f8:c0c:1::1")),
            new ZoneRecord("r3", new DnsRecord("www.operator.example", "A", 300, "81.2.69.9")));

        MailAuthenticationSuggestion suggestion = (await Suggest().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        suggestion.Spf.Value.ShouldBe("v=spf1 ip4:81.2.69.161 ip6:2a01:4f8:c0c:1::1 -all");
        suggestion.Source.ShouldBe(PublicAddressSource.Cloudflare);
        await _dns.DidNotReceiveWithAnyArgs().ResolveAsync(default!, Ct);
    }

    [Fact]
    public async Task The_addresses_are_the_same_whatever_order_dns_answers_in()
    {
        IPAddress[] answer = [IPAddress.Parse("2a01:4f8:c0c:1::1"), IPAddress.Parse("81.2.69.162"), IPAddress.Parse("81.2.69.160")];
        PublicAddresses addresses = new(new DnsTokenVault(_bed.Entities, new PrefixSealer()), _hosting, _dns, _bed.Options);

        _dns.ResolveAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<IPAddress>>.Success(answer));
        PublicAddressLookup first = await addresses.FindAsync(AdminTestBed.AcmeEntity, Ct);
        _dns.ResolveAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<IPAddress>>.Success([.. answer.Reverse()]));
        PublicAddressLookup second = await addresses.FindAsync(AdminTestBed.AcmeEntity, Ct);

        first.Addresses.Select(static a => a.ToString()).ShouldBe(["81.2.69.160", "81.2.69.162", "2a01:4f8:c0c:1::1"]);
        second.Addresses.ShouldBe(first.Addresses);
    }

    [Fact]
    public async Task A_host_cloudflare_proxies_names_no_addresses_and_does_not_fall_through_to_public_dns()
    {
        WithCloudflare(new ZoneRecord("r1", new DnsRecord(Host, "A", 1, "81.2.69.161"), Proxied: true));

        MailAuthenticationSuggestion suggestion = (await Suggest().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        suggestion.Spf.Value.ShouldBe("v=spf1 mx -all");
        suggestion.Source.ShouldBe(PublicAddressSource.Proxied);
        await _dns.DidNotReceiveWithAnyArgs().ResolveAsync(default!, Ct);
    }

    [Fact]
    public async Task A_host_public_dns_answers_from_cloudflares_edge_is_proxied_not_the_server()
    {
        _dns.ResolveAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<IPAddress>>.Success([IPAddress.Parse("104.21.32.1"), IPAddress.Parse("172.67.140.10")]));

        MailAuthenticationSuggestion suggestion = (await Suggest().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        suggestion.Spf.Value.ShouldBe("v=spf1 mx -all");
        suggestion.Source.ShouldBe(PublicAddressSource.Proxied);
        suggestion.Addresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_host_outside_every_zone_the_token_reaches_falls_through_to_public_dns()
    {
        WithCloudflare();
        _hosting.FindZoneAsync(default!, default!, Ct).ReturnsForAnyArgs(Error.NotFound("no zone"));

        MailAuthenticationSuggestion suggestion = (await Suggest().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        suggestion.Source.ShouldBe(PublicAddressSource.PublicDns);
        suggestion.Spf.Value.ShouldBe("v=spf1 ip4:81.2.69.160 -all");
    }

    [Fact]
    public async Task Private_addresses_and_failed_lookups_leave_the_mx_hosts_in_charge()
    {
        _dns.ResolveAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<IPAddress>>.Success([IPAddress.Parse("172.18.0.4")]));
        MailAuthenticationSuggestion privateOnly = (await Suggest().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;
        privateOnly.Spf.Value.ShouldBe("v=spf1 mx -all");
        privateOnly.Source.ShouldBe(PublicAddressSource.None);

        _dns.ResolveAsync(default!, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("timed out"));
        (await Suggest().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value.Spf.Value.ShouldBe("v=spf1 mx -all");
    }

    [Fact]
    public async Task Without_a_public_host_nothing_is_looked_up()
    {
        _bed.Options.PublicHost = null;

        MailAuthenticationSuggestion suggestion = (await Suggest().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        suggestion.Source.ShouldBe(PublicAddressSource.None);
        suggestion.Host.ShouldBeNull();
        await _dns.DidNotReceiveWithAnyArgs().ResolveAsync(default!, Ct);
    }

    [Fact]
    public async Task Only_a_global_administrator_is_offered_one()
    {
        _bed.ActAsEntityAdmin();

        (await Suggest().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _dns.DidNotReceiveWithAnyArgs().ResolveAsync(default!, Ct);
    }

    private sealed class PrefixSealer : ISecretSealer
    {
        public string Seal(string purpose, string secret) => "sealed:" + secret;

        public bool TryUnseal(string purpose, string sealedValue, out string secret)
        {
            secret = sealedValue.StartsWith("sealed:", StringComparison.Ordinal) ? sealedValue["sealed:".Length..] : string.Empty;
            return secret.Length > 0;
        }
    }
}
