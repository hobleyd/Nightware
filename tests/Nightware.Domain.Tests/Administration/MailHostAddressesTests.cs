using System.Net;

using Nightware.Domain.Administration;

namespace Nightware.Domain.Tests.Administration;

public sealed class MailHostAddressesTests
{
    private static readonly IPAddress V4 = IPAddress.Parse("81.2.69.160");
    private static readonly IPAddress V6 = IPAddress.Parse("2a01:4f8:c0c:1::1");

    private static MailDomain Acme => MailDomain.TryParse("acme.example", out MailDomain? d) ? d : throw new InvalidOperationException();

    private static DnsRecord Mx(string host) => new("acme.example", "MX", 3600, "10 " + host);

    [Fact]
    public void An_mx_host_in_the_domain_without_an_address_gets_one_record_per_public_address_after_its_mx()
    {
        DnsRecord dmarc = new("_dmarc.acme.example", "TXT", 3600, "v=DMARC1; p=reject");

        IReadOnlyList<DnsRecord> records = MailHostAddresses.AddTo(Acme, [Mx("mail.acme.example."), dmarc], [V4, V6, IPAddress.Parse("192.168.18.7")]);

        records.ShouldBe(
        [
            Mx("mail.acme.example."),
            new DnsRecord("mail.acme.example", "A", 3600, "81.2.69.160"),
            new DnsRecord("mail.acme.example", "AAAA", 3600, "2a01:4f8:c0c:1::1"),
            dmarc,
        ]);
    }

    [Fact]
    public void A_host_the_records_already_give_an_address_is_left_alone()
    {
        DnsRecord a = new("Mail.Acme.Example.", "A", 3600, "198.18.0.1");

        MailHostAddresses.Missing(Acme, [Mx("mail.acme.example"), a]).ShouldBeEmpty();
        MailHostAddresses.AddTo(Acme, [Mx("mail.acme.example"), a], [V4]).Count.ShouldBe(2);
    }

    [Fact]
    public void A_host_outside_the_domain_belongs_to_another_zone()
    {
        MailHostAddresses.Missing(Acme, [Mx("mail.operator.example"), Mx("notacme.example")]).ShouldBeEmpty();
    }

    [Fact]
    public void The_domain_itself_as_mx_host_is_inside_it()
    {
        MailHostAddresses.Missing(Acme, [Mx("acme.example")]).ShouldBe(["acme.example"]);
    }

    [Fact]
    public void Without_a_public_address_nothing_is_added()
    {
        IReadOnlyList<DnsRecord> records = [Mx("mail.acme.example")];

        MailHostAddresses.AddTo(Acme, records, [IPAddress.Parse("10.0.0.5")]).ShouldBeSameAs(records);
    }

    [Fact]
    public void Two_mx_records_for_one_host_add_its_addresses_once()
    {
        IReadOnlyList<DnsRecord> records = MailHostAddresses.AddTo(Acme, [Mx("mail.acme.example"), new DnsRecord("acme.example", "MX", 3600, "20 mail.acme.example")], [V4]);

        records.Count(r => r.Type == "A").ShouldBe(1);
    }
}
