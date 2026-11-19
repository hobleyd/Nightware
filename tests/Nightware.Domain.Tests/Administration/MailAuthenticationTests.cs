using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Domain.Tests.Administration;

public sealed class MailAuthenticationTests
{
    private static readonly MailDomain Acme = MailDomain.TryParse("acme.example", out MailDomain? d) ? d : throw new InvalidOperationException();

    private static SpfRecord Spf(string value) => SpfRecord.Parse(value).Value;

    private static DmarcRecord Dmarc(string value) => DmarcRecord.Parse(value).Value;

    private static readonly IReadOnlyList<DnsRecord> ServerRecords =
    [
        new("acme.example.", "MX", 3600, "10 mail.acme.example."),
        new("acme.example.", "TXT", 3600, "v=spf1 mx ra=postmaster -all"),
        new("mail.acme.example.", "TXT", 3600, "v=spf1 a ra=postmaster -all"),
        new("_dmarc.acme.example.", "TXT", 300, "v=DMARC1; p=reject; rua=mailto:postmaster@acme.example"),
        new("v1._domainkey.acme.example.", "TXT", 3600, "v=DKIM1; k=ed25519; p=abc"),
    ];

    [Theory]
    [InlineData("v=spf1 mx -all")]
    [InlineData("  v=spf1 mx include:_spf.mailchimp.example ~all ")]
    [InlineData("V=SPF1 -all")]
    [InlineData("v=spf1")]
    public void An_spf_record_starts_with_its_version(string value) =>
        SpfRecord.Parse(value).Value.Value.ShouldBe(value.Trim());

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("mx -all")]
    [InlineData("v=spf10 -all")]
    [InlineData("v=DMARC1; p=none")]
    [InlineData("\"v=spf1 mx -all\"")]
    [InlineData("v=spf1 mx\n-all")]
    [InlineData("v=spf1 a:café.example -all")]
    public void A_malformed_spf_record_is_refused(string? value)
    {
        Error error = SpfRecord.Parse(value).Error!;
        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Target.ShouldBe("spf");
    }

    [Fact]
    public void A_record_is_at_most_255_characters()
    {
        SpfRecord.Parse("v=spf1 " + new string('a', 248)).IsSuccess.ShouldBeTrue();
        SpfRecord.Parse("v=spf1 " + new string('a', 249)).Error!.Message.ShouldContain("at most 255");
    }

    [Theory]
    [InlineData("v=DMARC1; p=none", "none")]
    [InlineData("v=DMARC1; p=Quarantine; rua=mailto:a@acme.example,mailto:b@acme.example; pct=50;", "quarantine")]
    [InlineData("v=DMARC1;p=reject;sp=none;adkim=s;aspf=r;ruf=mailto:f@acme.example", "reject")]
    public void A_dmarc_record_carries_its_policy(string value, string policy)
    {
        DmarcRecord record = DmarcRecord.Parse(value).Value;
        record.Value.ShouldBe(value);
        record.Policy.ShouldBe(policy);
    }

    [Theory]
    [InlineData("p=reject", "starts with")]
    [InlineData("v=dmarc1; p=reject", "starts with")]
    [InlineData("v=DMARC1", "needs a policy")]
    [InlineData("v=DMARC1; rua=mailto:a@acme.example; p=reject", "straight after")]
    [InlineData("v=DMARC1; p=block", "none, quarantine or reject")]
    [InlineData("v=DMARC1; p=none; sp=maybe", "none, quarantine or reject")]
    [InlineData("v=DMARC1; p=none; rua=https://reports.example", "mailto:")]
    [InlineData("v=DMARC1; p=none; rua=mailto:a@acme.example, b@acme.example", "mailto:")]
    [InlineData("v=DMARC1; p=none; pct=150", "0 to 100")]
    [InlineData("v=DMARC1; p=none; junk", "not a DMARC tag")]
    public void A_malformed_dmarc_record_is_refused(string value, string reason)
    {
        Error error = DmarcRecord.Parse(value).Error!;
        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Target.ShouldBe("dmarc");
        error.Message.ShouldContain(reason);
    }

    [Fact]
    public void The_defaults_are_valid_records()
    {
        SpfRecord.Parse(SpfRecord.Default.Value).IsSuccess.ShouldBeTrue();
        DmarcRecord fallback = DmarcRecord.Default(Acme);
        fallback.Value.ShouldBe("v=DMARC1; p=reject; sp=reject; adkim=s; aspf=s; rua=mailto:postmaster@acme.example");
        fallback.Policy.ShouldBe("reject");
        DmarcRecord.Parse(fallback.Value).Value.ShouldBe(fallback);
    }

    [Fact]
    public void The_mail_servers_records_stand_when_nothing_was_chosen() =>
        DomainMailPolicy.MailServerDefault.ApplyTo(Acme, ServerRecords).ShouldBe(ServerRecords);

    [Fact]
    public void A_chosen_spf_replaces_the_apex_record_only_keeping_its_place_and_ttl()
    {
        var records = new DomainMailPolicy(Spf("v=spf1 mx include:mailer.example -all"), null).ApplyTo(Acme, ServerRecords);

        records.Count.ShouldBe(ServerRecords.Count);
        records[1].ShouldBe(new DnsRecord("acme.example.", "TXT", 3600, "v=spf1 mx include:mailer.example -all"));
        records[2].ShouldBe(ServerRecords[2]);
        records[3].ShouldBe(ServerRecords[3]);
    }

    [Fact]
    public void A_chosen_dmarc_replaces_the_servers()
    {
        var records = new DomainMailPolicy(null, Dmarc("v=DMARC1; p=none")).ApplyTo(Acme, ServerRecords);

        records.Where(r => r.Name.StartsWith("_dmarc", StringComparison.Ordinal)).ShouldHaveSingleItem().ShouldBe(new DnsRecord("_dmarc.acme.example.", "TXT", 300, "v=DMARC1; p=none"));
        DomainMailPolicy.DmarcIn(Acme, records).ShouldBe("v=DMARC1; p=none");
        DomainMailPolicy.SpfIn(Acme, records).ShouldBe("v=spf1 mx ra=postmaster -all");
    }

    [Fact]
    public void Duplicate_spf_records_collapse_to_one()
    {
        IReadOnlyList<DnsRecord> doubled = [.. ServerRecords, new DnsRecord("ACME.example", "txt", null, "v=spf1 -all")];

        var records = new DomainMailPolicy(Spf("v=spf1 mx -all"), null).ApplyTo(Acme, doubled);

        records.Count(r => r.Value.StartsWith("v=spf1", StringComparison.Ordinal) && r.Name.TrimEnd('.') == "acme.example").ShouldBe(1);
    }

    [Fact]
    public void Missing_spf_and_dmarc_records_get_the_defaults()
    {
        IReadOnlyList<DnsRecord> mxOnly = [ServerRecords[0]];

        var records = DomainMailPolicy.MailServerDefault.ApplyTo(Acme, mxOnly);

        records.ShouldBe(
        [
            ServerRecords[0],
            new DnsRecord("acme.example", "TXT", null, SpfRecord.Default.Value),
            new DnsRecord("_dmarc.acme.example", "TXT", null, DmarcRecord.Default(Acme).Value),
        ]);
    }

    [Fact]
    public void Missing_spf_names_the_servers_public_address_when_it_is_known()
    {
        IReadOnlyList<DnsRecord> mxOnly = [ServerRecords[0]];

        var records = DomainMailPolicy.MailServerDefault.ApplyTo(Acme, mxOnly, [System.Net.IPAddress.Parse("81.2.69.160")]);

        DomainMailPolicy.SpfIn(Acme, records).ShouldBe("v=spf1 ip4:81.2.69.160 -all");
    }

    [Fact]
    public void The_spf_fallback_is_needed_only_when_neither_the_server_nor_the_domain_has_one()
    {
        IReadOnlyList<DnsRecord> mxOnly = [ServerRecords[0]];

        DomainMailPolicy.MailServerDefault.NeedsSpfFallback(Acme, mxOnly).ShouldBeTrue();
        DomainMailPolicy.MailServerDefault.NeedsSpfFallback(Acme, ServerRecords).ShouldBeFalse();
        new DomainMailPolicy(Spf("v=spf1 mx -all"), null).NeedsSpfFallback(Acme, mxOnly).ShouldBeFalse();
        DomainMailPolicy.MailServerDefault.NeedsSpfFallback(Acme, []).ShouldBeFalse();
    }

    [Fact]
    public void A_domain_without_records_stays_without() =>
        new DomainMailPolicy(Spf("v=spf1 -all"), null).ApplyTo(Acme, []).ShouldBeEmpty();

    [Fact]
    public void An_entity_without_a_choice_for_a_domain_uses_the_mail_server_default()
    {
        Entity entity = new("e", "acme", "Acme", [Acme]);
        entity.MailPolicyFor(Acme).ShouldBeSameAs(DomainMailPolicy.MailServerDefault);
        DomainMailPolicy.MailServerDefault.IsDefault.ShouldBeTrue();

        DomainMailPolicy chosen = new(null, Dmarc("v=DMARC1; p=none"));
        (entity with { MailPolicies = new Dictionary<MailDomain, DomainMailPolicy> { [Acme] = chosen } }).MailPolicyFor(Acme).ShouldBe(chosen);
        chosen.IsDefault.ShouldBeFalse();
    }

    [Fact]
    public void Entities_without_choices_compare_equal()
    {
        MailDomain[] domains = [Acme];
        new Entity("e", "acme", "Acme", domains).ShouldBe(new Entity("e", "acme", "Acme", domains));
    }

    [Fact]
    public void An_entity_names_the_user_receiving_each_domains_mail_to_unknown_addresses()
    {
        MailDomain other = MailDomain.TryParse("other.example", out MailDomain? o) ? o : throw new InvalidOperationException();
        MailDomain third = MailDomain.TryParse("a.example", out MailDomain? t) ? t : throw new InvalidOperationException();
        Entity entity = new("e", "acme", "Acme", [Acme, other, third]);
        entity.CatchAllFor(Acme).ShouldBeNull();
        entity.CatchAllsOf("u-1").ShouldBeEmpty();

        Entity chosen = entity with { CatchAlls = new Dictionary<MailDomain, string> { [Acme] = "u-1", [other] = "u-2", [third] = "u-1" } };

        chosen.CatchAllFor(Acme).ShouldBe("u-1");
        chosen.CatchAllFor(other).ShouldBe("u-2");
        chosen.CatchAllsOf("u-1").ShouldBe([third, Acme]);
    }
}
