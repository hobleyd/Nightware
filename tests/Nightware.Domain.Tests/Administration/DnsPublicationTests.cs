using Nightware.Domain.Administration;

namespace Nightware.Domain.Tests.Administration;

public sealed class DnsPublicationTests
{
    private static readonly DnsZone Zone = new("z1", "acme.example");

    private static DnsRecord R(string name, string type, string value, int? ttl = 3600) => new(name, type, ttl, value);

    private static ZoneRecord Z(string id, string name, string type, string value, bool proxied = false) => new(id, new DnsRecord(name, type, null, value), proxied);

    private static DnsChange Single(DnsRecord desired, params ZoneRecord[] existing) =>
        DnsPublicationPlanner.Plan(Zone, [desired], existing).Changes.ShouldHaveSingleItem();

    [Fact]
    public void A_zone_that_already_has_every_record_needs_no_work()
    {
        DnsRecord[] desired =
        [
            R("acme.example", "MX", "10 mail.acme.example"),
            R("acme.example", "TXT", "v=spf1 mx -all"),
            R("_jmap._tcp.acme.example", "SRV", "0 1 443 mail.acme.example"),
            R("autoconfig.acme.example", "CNAME", "mail.acme.example"),
            R("_25._tcp.mail.acme.example", "TLSA", "3 1 1 2A54C5B2 D6B1A0E9"),
        ];
        ZoneRecord[] existing =
        [
            Z("1", "acme.example", "MX", "10 Mail.Acme.Example."),
            Z("2", "acme.example", "TXT", "\"v=spf1 mx -all\""),
            Z("3", "_jmap._tcp.acme.example", "SRV", "0 1 443 mail.acme.example"),
            Z("4", "autoconfig.acme.example", "CNAME", "mail.acme.example"),
            Z("5", "_25._tcp.mail.acme.example", "TLSA", "3 1 1 2a54c5b2d6b1a0e9"),
        ];

        DnsPublicationPlan plan = DnsPublicationPlanner.Plan(Zone, desired, existing);

        plan.HasWork.ShouldBeFalse();
        plan.Count(DnsChangeKind.Unchanged).ShouldBe(5);
        plan.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_record_is_created_in_canonical_form()
    {
        DnsChange change = Single(R("Acme.Example.", "mx", "10 Mail.Acme.Example."));

        change.Kind.ShouldBe(DnsChangeKind.Create);
        change.Desired.ShouldBe(R("acme.example", "MX", "10 mail.acme.example"));
    }

    [Fact]
    public void The_spf_record_replaces_the_zones_and_a_second_one_is_removed()
    {
        DnsPublicationPlan plan = DnsPublicationPlanner.Plan(Zone, [R("acme.example", "TXT", "v=spf1 mx -all")],
        [
            Z("site", "acme.example", "TXT", "google-site-verification=abc"),
            Z("old", "acme.example", "TXT", "v=spf1 include:_spf.google.com ~all"),
            Z("dup", "acme.example", "TXT", "\"v=spf1 a -all\""),
        ]);

        plan.Changes.Select(static c => (c.Kind, c.Current?.Id)).ShouldBe([(DnsChangeKind.Update, "old"), (DnsChangeKind.Remove, "dup")]);
    }

    [Fact]
    public void A_long_dkim_key_split_into_strings_matches_the_joined_value()
    {
        string key = "v=DKIM1; k=rsa; p=" + new string('A', 300);
        string split = $"\"{key[..255]}\" \"{key[255..]}\"";

        Single(R("s1._domainkey.acme.example", "TXT", key), Z("k", "s1._domainkey.acme.example", "TXT", split)).Kind.ShouldBe(DnsChangeKind.Unchanged);
    }

    [Fact]
    public void A_txt_record_without_a_version_tag_is_added_beside_others()
    {
        Single(R("_mta-sts.acme.example", "TXT", "hello"), Z("x", "_mta-sts.acme.example", "TXT", "other")).Kind.ShouldBe(DnsChangeKind.Create);
    }

    [Fact]
    public void An_mx_to_the_same_host_has_its_priority_corrected()
    {
        DnsChange change = Single(R("acme.example", "MX", "10 mail.acme.example"), Z("m", "acme.example", "MX", "20 mail.acme.example"));

        change.Kind.ShouldBe(DnsChangeKind.Update);
        change.Current!.Id.ShouldBe("m");
    }

    [Fact]
    public void Another_servers_mx_is_removed_so_all_the_domains_mail_comes_here()
    {
        DnsPublicationPlan plan = DnsPublicationPlanner.Plan(Zone, [R("acme.example", "MX", "10 mail.acme.example")], [Z("g", "acme.example", "MX", "1 smtp.google.com")]);

        plan.Changes.Select(static c => c.Kind).ShouldBe([DnsChangeKind.Create, DnsChangeKind.Remove]);
        DnsChange removed = plan.Changes[1];
        removed.Current!.Id.ShouldBe("g");
        removed.Note!.ShouldContain("smtp.google.com");
        plan.MailServersRemoved.ShouldBe(["smtp.google.com"]);
        plan.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void An_mx_at_a_name_the_domain_publishes_no_mx_for_is_left_alone()
    {
        DnsPublicationPlan plan = DnsPublicationPlanner.Plan(Zone, [R("acme.example", "MX", "10 mail.acme.example")], [Z("s", "shop.acme.example", "MX", "1 smtp.google.com")]);

        plan.Changes.ShouldHaveSingleItem().Kind.ShouldBe(DnsChangeKind.Create);
        plan.MailServersRemoved.ShouldBeEmpty();
    }

    [Fact]
    public void The_domains_own_mx_with_another_priority_is_corrected_not_removed()
    {
        DnsPublicationPlan plan = DnsPublicationPlanner.Plan(Zone, [R("acme.example", "MX", "10 mail.acme.example")], [Z("m", "acme.example", "MX", "20 mail.acme.example")]);

        plan.MailServersRemoved.ShouldBeEmpty();
        plan.Changes.ShouldHaveSingleItem().Kind.ShouldBe(DnsChangeKind.Update);
    }

    [Fact]
    public void An_srv_record_to_the_same_port_and_host_is_corrected()
    {
        Single(R("_imaps._tcp.acme.example", "SRV", "0 1 993 mail.acme.example"), Z("s", "_imaps._tcp.acme.example", "SRV", "5 0 993 mail.acme.example"))
            .Kind.ShouldBe(DnsChangeKind.Update);
    }

    [Fact]
    public void A_first_caa_record_is_not_published_but_one_beside_others_is()
    {
        DnsRecord caa = R("acme.example", "CAA", "0 issue \"letsencrypt.org\"");

        Single(caa).Kind.ShouldBe(DnsChangeKind.Skip);
        Single(caa, Z("c", "acme.example", "CAA", "0 issue \"digicert.com\"")).Kind.ShouldBe(DnsChangeKind.Create);
        Single(caa, Z("c", "acme.example", "CAA", "0 ISSUE \"letsencrypt.org\"")).Kind.ShouldBe(DnsChangeKind.Unchanged);
    }

    [Fact]
    public void A_cname_is_not_published_beside_other_records_and_nothing_is_published_beside_a_cname()
    {
        Single(R("autoconfig.acme.example", "CNAME", "mail.acme.example"), Z("a", "autoconfig.acme.example", "A", "192.0.2.1"))
            .ShouldSatisfyAllConditions(c => c.Kind.ShouldBe(DnsChangeKind.Skip), c => c.Note!.ShouldContain("A record"));
        Single(R("mail.acme.example", "TXT", "v=spf1 a -all"), Z("c", "mail.acme.example", "CNAME", "host.example.net"))
            .Kind.ShouldBe(DnsChangeKind.Skip);
    }

    [Fact]
    public void A_cname_to_another_target_or_behind_the_proxy_is_corrected()
    {
        Single(R("autoconfig.acme.example", "CNAME", "mail.acme.example"), Z("c", "autoconfig.acme.example", "CNAME", "old.acme.example")).Kind.ShouldBe(DnsChangeKind.Update);
        DnsChange proxied = Single(R("autoconfig.acme.example", "CNAME", "mail.acme.example"), Z("c", "autoconfig.acme.example", "CNAME", "mail.acme.example", proxied: true));
        proxied.Kind.ShouldBe(DnsChangeKind.Update);
        proxied.Note!.ShouldContain("proxy");
    }

    [Fact]
    public void A_record_outside_the_zone_is_not_published()
    {
        Single(R("_25._tcp.mail.host.example", "TLSA", "3 1 1 AB")).ShouldSatisfyAllConditions(
            c => c.Kind.ShouldBe(DnsChangeKind.Skip),
            c => c.Note!.ShouldContain("Outside the acme.example zone"));
        Single(R("notacme.example", "TXT", "v=spf1 -all")).Kind.ShouldBe(DnsChangeKind.Skip);
    }

    [Fact]
    public void The_fingerprint_is_stable_and_follows_the_zone()
    {
        DnsRecord[] desired = [R("acme.example", "TXT", "v=spf1 mx -all")];
        string first = DnsPublicationPlanner.Plan(Zone, desired, []).Fingerprint;

        DnsPublicationPlanner.Plan(Zone, desired, []).Fingerprint.ShouldBe(first);
        DnsPublicationPlanner.Plan(Zone, desired, [Z("1", "acme.example", "TXT", "v=spf1 -all")]).Fingerprint.ShouldNotBe(first);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("quote\"", false)]
    [InlineData("  AbC123_-xyz0987654321AbC123_-xyz09876543  ", true)]
    public void A_token_is_one_word(string value, bool valid) =>
        DnsProviderToken.Parse(value).IsSuccess.ShouldBe(valid);

    [Fact]
    public void A_token_is_bounded_and_never_printed()
    {
        DnsProviderToken.Parse(new string('a', DnsProviderToken.MaxLength + 1)).IsSuccess.ShouldBeFalse();
        DnsProviderToken token = DnsProviderToken.Parse("secret-token-value").Value;

        token.ToString().ShouldNotContain("secret");
        $"{token}".ShouldNotContain("secret");
    }
}
