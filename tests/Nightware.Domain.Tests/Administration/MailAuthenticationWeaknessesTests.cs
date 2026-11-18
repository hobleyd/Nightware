using Nightware.Domain.Administration;

namespace Nightware.Domain.Tests.Administration;

public sealed class MailAuthenticationWeaknessesTests
{
    private static IReadOnlyList<MailAuthenticationWeakness> Txt(string name, string value) =>
        MailAuthenticationWeaknesses.Of(new DnsRecord(name, "TXT", 3600, value));

    [Theory]
    [InlineData("v=spf1 ip4:81.2.69.160 -all")]
    [InlineData("v=spf1 mx -all")]
    [InlineData("v=spf1 redirect=_spf.example.com")]
    public void A_strict_spf_record_has_no_weakness(string value) =>
        Txt("acme.example", value).ShouldBeEmpty();

    [Theory]
    [InlineData("v=spf1 mx +all", WeaknessSeverity.Unsafe, "+all")]
    [InlineData("v=spf1 mx all", WeaknessSeverity.Unsafe, "+all")]
    [InlineData("v=spf1 mx ?all", WeaknessSeverity.Weak, "?all")]
    [InlineData("v=spf1 mx ~all", WeaknessSeverity.Weak, "~all")]
    [InlineData("v=spf1 mx", WeaknessSeverity.Weak, "No all")]
    [InlineData("v=spf1 ptr -all", WeaknessSeverity.Weak, "ptr")]
    public void A_permissive_spf_record_is_flagged_with_what_it_lets_through(string value, WeaknessSeverity severity, string mentions)
    {
        MailAuthenticationWeakness weakness = Txt("acme.example", value).ShouldHaveSingleItem();

        weakness.Severity.ShouldBe(severity);
        weakness.Message.ShouldContain(mentions);
    }

    [Fact]
    public void The_hardened_dmarc_record_has_no_weakness() =>
        Txt("_dmarc.acme.example", "v=DMARC1; p=reject; sp=reject; adkim=s; aspf=s; rua=mailto:postmaster@acme.example").ShouldBeEmpty();

    [Fact]
    public void A_lax_dmarc_record_names_each_thing_it_leaves_open()
    {
        IReadOnlyList<MailAuthenticationWeakness> found = Txt("_dmarc.acme.example", "\"v=DMARC1; p=none; sp=none; pct=50\"");

        found.Select(static w => w.Message).ShouldBe(
        [
            "p=none only reports mail that fails; it is still delivered. p=reject refuses it.",
            "sp=none lets failing mail claiming a subdomain through.",
            "pct=50 applies the policy to only 50% of failing mail.",
            "No rua= address, so nobody receives reports of mail that fails.",
        ]);
        found.ShouldAllBe(static w => w.Severity == WeaknessSeverity.Weak);
    }

    [Fact]
    public void Records_that_are_not_spf_or_dmarc_are_not_judged()
    {
        MailAuthenticationWeaknesses.Of(new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example")).ShouldBeEmpty();
        Txt("x._domainkey.acme.example", "v=DKIM1; k=rsa; p=abc").ShouldBeEmpty();
        Txt("acme.example", "google-site-verification=abc").ShouldBeEmpty();
    }
}
