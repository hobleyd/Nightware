using Nightware.Adapters.Jmap.Administration;
using Nightware.Domain.Administration;

namespace Nightware.Adapters.Jmap.Tests.Administration;

/// <summary>
/// Hand-written zones in the shape a mail server publishes for a domain. No <c>dnsZoneFile</c> has been
/// recorded from Stalwart yet (phase 8 step 0b); replace or extend <see cref="MailZone"/> with that sample.
/// </summary>
public sealed class ZoneFileParserTests
{
    private const string DkimRsaKey =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAu1SU1LfVLPHCozMxH2Mo4lgOEePzNm0tRgeLezV6ffAt0gunVTLw7onLRnrq0/IzW7yWR7QkrmBL7jTKEn5u+qKhbwKfBstIs+bMY2Zkp18gnTxKLxoS2tFczGkPLPgizskuemMghRniWaoLcyehkd3qqGElvW/VDL5AaWTg0nLVkjRo9z+40RQzuVaE8AkAFmxZzow3x+VJYKdx/Dtr0yrXyZ8AZBVqdIImGh+jwyfvgr8NdbTAnFIqIHdmFJGtl7AfCYH4Gzh6CQi9e6Hc2HtVWdYp8ubwyJJQzk02r9Pt/xHtm8pi7tHIKbq/VlJUfabcnBOJZDGShlaJ5nhvwIDAQAB";

    private static readonly string MailZone = $$"""
        ; Zone data for acme.example
        $ORIGIN acme.example.
        $TTL 1h
        @                   IN  MX     10 mail.acme.example.
        @                   IN  TXT    "v=spf1 mx ra=postmaster -all"
        mail                IN  TXT    "v=spf1 a ra=postmaster -all"
        _dmarc        3600  IN  TXT    "v=DMARC1; p=reject; rua=mailto:postmaster@acme.example; ruf=mailto:postmaster@acme.example"
        v1-rsa-20261117._domainkey IN TXT ( "v=DKIM1; k=rsa; h=sha256; "
                                            "p={{DkimRsaKey[..200]}}"
                                            "{{DkimRsaKey[200..]}}" ) ; split: >255 bytes
        v1-ed25519-20261117._domainkey IN 86400 TXT "v=DKIM1; k=ed25519; h=sha256; p=11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo="
        _jmap._tcp          IN  SRV    0 1 443 mail.acme.example.
        _imaps._tcp         IN  SRV    0 1 993 mail
        _submissions._tcp   IN  SRV    0 1 465 mail
        _pop3s._tcp         IN  SRV    0 0 0 .
        autoconfig          IN  CNAME  mail
        autodiscover        IN  CNAME  mail.acme.example.
        mta-sts             IN  CNAME  mail
        _mta-sts            IN  TXT    "v=STSv1; id=8203645182093284726"
        _smtp._tls          IN  TXT    "v=TLSRPTv1; rua=mailto:postmaster@acme.example"
        @                   IN  CAA    0 issue "letsencrypt.org"
                            IN  CAA    0 iodef "mailto:postmaster@acme.example"
        _25._tcp.mail       IN  TLSA   3 1 1 ( 2A54C5B2D6B1A0E9B6D6E8E6F00F5A2D
                                               9C1E3D1C5A0E9F2B1C0D9E8F7A6B5C4D )
        """;

    private static IReadOnlyList<DnsRecord> Parse(string zone, string origin = "acme.example") => ZoneFileParser.Parse(zone, origin);

    private static DnsRecord Single(string name, string type) =>
        Parse(MailZone).Single(r => r.Name == name && r.Type == type);

    [Fact]
    public void Reads_every_record_of_a_mail_zone()
    {
        var records = ZoneFileParser.Parse(MailZone, "acme.example", out int skipped);

        skipped.ShouldBe(0);
        records.Count.ShouldBe(18);
        records.Select(r => r.Type).Distinct().ShouldBe(["MX", "TXT", "SRV", "CNAME", "CAA", "TLSA"], ignoreOrder: true);
    }

    [Fact]
    public void Joins_a_dkim_key_split_over_strings_and_lines()
    {
        var dkim = Single("v1-rsa-20261117._domainkey.acme.example", "TXT");

        dkim.Value.ShouldBe($"v=DKIM1; k=rsa; h=sha256; p={DkimRsaKey}");
        dkim.Ttl.ShouldBe(3600);
    }

    [Fact]
    public void Semicolons_inside_quotes_are_not_comments()
    {
        Single("_dmarc.acme.example", "TXT").Value.ShouldBe("v=DMARC1; p=reject; rua=mailto:postmaster@acme.example; ruf=mailto:postmaster@acme.example");
    }

    [Fact]
    public void Reads_spf_at_the_apex_and_on_the_host()
    {
        Single("acme.example", "TXT").Value.ShouldBe("v=spf1 mx ra=postmaster -all");
        Single("mail.acme.example", "TXT").Value.ShouldBe("v=spf1 a ra=postmaster -all");
    }

    [Fact]
    public void Reads_mx_with_a_qualified_exchange()
    {
        Single("acme.example", "MX").ShouldBe(new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"));
    }

    [Fact]
    public void Qualifies_relative_srv_and_cname_targets()
    {
        Single("_jmap._tcp.acme.example", "SRV").Value.ShouldBe("0 1 443 mail.acme.example");
        Single("_imaps._tcp.acme.example", "SRV").Value.ShouldBe("0 1 993 mail.acme.example");
        Single("_pop3s._tcp.acme.example", "SRV").Value.ShouldBe("0 0 0 .");
        Single("autoconfig.acme.example", "CNAME").Value.ShouldBe("mail.acme.example");
        Single("autodiscover.acme.example", "CNAME").Value.ShouldBe("mail.acme.example");
    }

    [Fact]
    public void Accepts_ttl_and_class_in_either_order()
    {
        Single("v1-ed25519-20261117._domainkey.acme.example", "TXT").Ttl.ShouldBe(86400);
        Single("_dmarc.acme.example", "TXT").Ttl.ShouldBe(3600);
    }

    [Fact]
    public void A_blank_owner_repeats_the_previous_one_and_caa_keeps_its_quotes()
    {
        var caa = Parse(MailZone).Where(r => r.Type == "CAA").ToList();

        caa.Select(r => r.Name).ShouldBe(["acme.example", "acme.example"]);
        caa.Select(r => r.Value).ShouldBe(["0 issue \"letsencrypt.org\"", "0 iodef \"mailto:postmaster@acme.example\""]);
    }

    [Fact]
    public void Joins_a_tlsa_record_continued_in_parentheses()
    {
        Single("_25._tcp.mail.acme.example", "TLSA").Value.ShouldBe("3 1 1 2A54C5B2D6B1A0E9B6D6E8E6F00F5A2D 9C1E3D1C5A0E9F2B1C0D9E8F7A6B5C4D");
    }

    [Fact]
    public void Uses_the_given_origin_without_an_origin_directive()
    {
        Parse("_dmarc 300 IN TXT \"v=DMARC1; p=none\"\n@ IN MX 10 mail\n", "Acme.Example.")
            .ShouldBe([
                new DnsRecord("_dmarc.Acme.Example", "TXT", 300, "v=DMARC1; p=none"),
                new DnsRecord("Acme.Example", "MX", 300, "10 mail.Acme.Example"),
            ]);
    }

    [Fact]
    public void A_later_origin_applies_to_the_names_after_it()
    {
        Parse("$ORIGIN _domainkey.acme.example.\ns1 IN TXT \"v=DKIM1; p=abc\"\n$ORIGIN sub\nwww IN CNAME @\n")
            .ShouldBe([
                new DnsRecord("s1._domainkey.acme.example", "TXT", null, "v=DKIM1; p=abc"),
                new DnsRecord("www.sub._domainkey.acme.example", "CNAME", null, "sub._domainkey.acme.example"),
            ]);
    }

    [Fact]
    public void A_default_ttl_beats_the_previous_explicit_ttl()
    {
        Parse("a 60 IN A 192.0.2.1\nb IN A 192.0.2.2\n$TTL 1d2h\nc IN A 192.0.2.3\n")
            .Select(r => r.Ttl).ShouldBe([60, 60, 93600]);
    }

    [Fact]
    public void Unescapes_quoted_strings()
    {
        Parse("t IN TXT \"say \\\"hi\\\" \\\\ \\065\\066\" unquoted\\;part\n").Single().Value
            .ShouldBe("say \"hi\" \\ ABunquoted;part");
    }

    [Fact]
    public void Handles_windows_line_endings_and_a_missing_final_newline()
    {
        Parse("@ IN MX 10 mail\r\n@ IN TXT \"v=spf1 -all\"").Select(r => r.Value).ShouldBe(["10 mail.acme.example", "v=spf1 -all"]);
    }

    [Fact]
    public void Skips_lines_it_cannot_read()
    {
        var records = ZoneFileParser.Parse("$INCLUDE other.zone\ngarbage\n   ; only a comment\n\n@ IN\nok IN TXT \"v\"\n", "acme.example", out int skipped);

        records.ShouldBe([new DnsRecord("ok.acme.example", "TXT", null, "v")]);
        skipped.ShouldBe(3);
    }

    [Fact]
    public void An_empty_zone_has_no_records()
    {
        Parse(string.Empty).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("3600", 3600)]
    [InlineData("1h", 3600)]
    [InlineData("1H30M", 5400)]
    [InlineData("1w", 604800)]
    [InlineData("1d1", 86401)]
    public void Reads_ttl_units(string token, int seconds)
    {
        ZoneFileParser.TryParseTtl(token, out int parsed).ShouldBeTrue();
        parsed.ShouldBe(seconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("h1")]
    [InlineData("1x")]
    [InlineData("1hh")]
    [InlineData("99999999999")]
    public void Rejects_bad_ttls(string token)
    {
        ZoneFileParser.TryParseTtl(token, out _).ShouldBeFalse();
    }
}