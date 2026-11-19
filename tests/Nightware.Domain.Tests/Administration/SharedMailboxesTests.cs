using Nightware.Domain.Administration;

namespace Nightware.Domain.Tests.Administration;

public sealed class SharedMailboxesTests
{
    private static MailAddress Address(string value) => MailAddress.TryParse(value, out MailAddress? a) ? a : throw new ArgumentException(value);

    [Fact]
    public void A_shared_mailbox_answers_to_its_address_then_its_aliases()
    {
        var mailbox = new SharedMailbox("s1", "e1", Address("support@acme.example"), "Support") { Aliases = [Address("help@acme.example")] };

        mailbox.Addresses.Select(static a => a.Value).ShouldBe(["support@acme.example", "help@acme.example"]);
    }

    [Fact]
    public void Without_aliases_it_answers_to_its_address_only()
    {
        new SharedMailbox("s1", "e1", Address("support@acme.example"), "Support").Addresses.ShouldHaveSingleItem().Value.ShouldBe("support@acme.example");
        new NewSharedMailbox(Address("support@acme.example"), "Support").Aliases.ShouldBeEmpty();
    }
}
