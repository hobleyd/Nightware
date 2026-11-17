using System.Net;

using Nightware.Domain.Administration;

namespace Nightware.Domain.Tests.Administration;

public sealed class SecureMailDefaultsTests
{
    private static MailDomain Acme => MailDomain.TryParse("acme.example", out MailDomain? d) ? d : throw new InvalidOperationException();

    [Fact]
    public void Spf_names_only_the_servers_public_addresses_and_refuses_the_rest()
    {
        SpfRecord spf = SecureMailDefaults.Spf([IPAddress.Parse("81.2.69.160"), IPAddress.Parse("2a01:4f8:c0c:1::1")]);

        spf.Value.ShouldBe("v=spf1 ip4:81.2.69.160 ip6:2a01:4f8:c0c:1::1 -all");
    }

    [Fact]
    public void Spf_without_a_public_address_falls_back_to_the_mx_hosts()
    {
        SecureMailDefaults.Spf([]).Value.ShouldBe("v=spf1 mx -all");
        SecureMailDefaults.Spf([IPAddress.Parse("172.18.0.5"), IPAddress.Loopback]).Value.ShouldBe("v=spf1 mx -all");
    }

    [Fact]
    public void Spf_drops_duplicates_and_stays_inside_one_txt_string()
    {
        IPAddress[] many = [.. Enumerable.Range(1, 20).Select(i => IPAddress.Parse($"81.2.69.{i}")), IPAddress.Parse("81.2.69.1")];

        SpfRecord spf = SecureMailDefaults.Spf(many);

        spf.Value.Split(' ').Count(m => m.StartsWith("ip4:", StringComparison.Ordinal)).ShouldBe(SecureMailDefaults.MaxAddresses);
        SpfRecord.Parse(spf.Value).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Dmarc_rejects_failing_mail_with_strict_alignment_and_reports_to_the_postmaster()
    {
        DmarcRecord dmarc = SecureMailDefaults.Dmarc(Acme);

        dmarc.Value.ShouldBe("v=DMARC1; p=reject; sp=reject; adkim=s; aspf=s; rua=mailto:postmaster@acme.example");
        dmarc.Policy.ShouldBe("reject");
        DmarcRecord.Parse(dmarc.Value).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("81.2.69.160", true)]
    [InlineData("2a01:4f8:c0c:1::1", true)]
    [InlineData("::ffff:81.2.69.160", true)]
    [InlineData("10.0.0.4", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.10.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("192.0.2.7", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    public void Only_addresses_reachable_from_the_internet_count_as_public(string address, bool expected)
    {
        SecureMailDefaults.IsPublic(IPAddress.Parse(address)).ShouldBe(expected);
    }
}
