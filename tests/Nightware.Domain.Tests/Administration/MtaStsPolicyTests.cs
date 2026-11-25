using Nightware.Domain.Administration;

namespace Nightware.Domain.Tests.Administration;

public sealed class MtaStsPolicyTests
{
    [Fact]
    public void Without_hosts_of_its_own_the_policy_lists_the_mail_servers_defaults()
    {
        new MtaStsPolicy("testing", [], ["mail.server.example"]).Hosts.ShouldBe(["mail.server.example"]);
        new MtaStsPolicy("testing", ["mail.acme.example"], ["mail.server.example"]).Hosts.ShouldBe(["mail.acme.example"]);
    }

    [Theory]
    [InlineData("mail.acme.example", true)]
    [InlineData("MAIL.acme.example.", true)]
    [InlineData("mx.other.example", true)]
    [InlineData("a.b.other.example", false)]
    [InlineData("other.example", false)]
    [InlineData("mail.stranger.example", false)]
    public void A_host_is_covered_by_its_name_or_a_wildcard_one_label_deep(string host, bool covered) =>
        new MtaStsPolicy("enforce", ["mail.acme.example", "*.other.example"], []).Covers(host).ShouldBe(covered);

    [Fact]
    public void Missing_lists_each_uncovered_host_once()
    {
        var policy = new MtaStsPolicy("testing", [], ["mail.server.example"]);

        policy.Missing(["mail.acme.example.", "MAIL.acme.example", "mail.server.example"]).ShouldBe(["mail.acme.example"]);
    }

    [Fact]
    public void Adding_to_a_policy_without_hosts_keeps_the_defaults_it_served()
    {
        new MtaStsPolicy("testing", [], ["mail.server.example"]).Adding(["mail.acme.example"]).ShouldBe(["mail.server.example", "mail.acme.example"]);
        new MtaStsPolicy("testing", ["mail.acme.example"], ["mail.server.example"]).Adding(["mail.other.example", "mail.acme.example"]).ShouldBe(["mail.acme.example", "mail.other.example"]);
    }

    [Fact]
    public void Disable_serves_no_policy()
    {
        new MtaStsPolicy("disable", [], ["mail.server.example"]).IsDisabled.ShouldBeTrue();
        new MtaStsPolicy("testing", [], ["mail.server.example"]).IsDisabled.ShouldBeFalse();
    }

    [Fact]
    public void A_domains_MX_hosts_are_read_from_its_MX_records()
    {
        MtaStsPolicy.MxHostsIn(
        [
            new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example."),
            new DnsRecord("acme.example", "MX", 3600, "20 backup.acme.example"),
            new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all"),
            new DnsRecord("acme.example", "mx", 3600, "10 Mail.Acme.Example"),
        ]).ShouldBe(["mail.acme.example", "backup.acme.example"]);
    }
}
