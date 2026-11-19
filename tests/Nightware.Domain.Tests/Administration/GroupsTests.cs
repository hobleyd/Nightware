using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Domain.Tests.Administration;

public sealed class GroupsTests
{
    [Theory]
    [InlineData("finance", "finance")]
    [InlineData(" Finance-Ops2 ", "finance-ops2")]
    public void A_key_is_normalised(string input, string expected)
    {
        GroupKey.TryParse(input, out GroupKey? key).ShouldBeTrue();
        key!.Value.ShouldBe(expected);
        key.ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2fast")]
    [InlineData("-finance")]
    [InlineData("finance-")]
    [InlineData("fin--ance")]
    [InlineData("fin_ance")]
    [InlineData("fin ance")]
    public void A_malformed_key_is_refused(string? input) =>
        GroupKey.TryParse(input, out _).ShouldBeFalse();

    [Fact]
    public void A_key_over_40_characters_is_refused() =>
        GroupKey.TryParse(new string('a', 41), out _).ShouldBeFalse();

    [Theory]
    [InlineData("Finance", "finance")]
    [InlineData("Finance & Ops", "finance-ops")]
    [InlineData("  Café  Team! ", "cafe-team")]
    [InlineData("2026 Board", "board")]
    public void A_key_is_proposed_from_a_name(string name, string expected) =>
        GroupKey.Propose(name)!.Value.ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("——")]
    public void No_key_is_proposed_from_a_name_without_letters(string? name) =>
        GroupKey.Propose(name).ShouldBeNull();

    [Fact]
    public void A_proposed_key_is_cut_to_the_maximum_without_a_trailing_hyphen()
    {
        GroupKey key = GroupKey.Propose(new string('a', 39) + " b")!;
        key.Value.ShouldBe(new string('a', 39));
    }

    [Fact]
    public void The_groupware_id_carries_the_entity_alias() =>
        Key("finance").GroupwareId(" Acme ").ShouldBe("acme-finance");

    [Theory]
    [InlineData("Accountant+Lists@Example.NET", "accountant+lists@example.net")]
    [InlineData(" o'brien@example.com ", "o'brien@example.com")]
    public void A_recipient_address_is_normalised(string input, string expected)
    {
        RecipientAddress.TryParse(input, out RecipientAddress? address).ShouldBeTrue();
        address!.Value.ShouldBe(expected);
        address.ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nobody")]
    [InlineData("@example.com")]
    [InlineData("a\"b@example.com")]
    [InlineData("a b@example.com")]
    [InlineData(".a@example.com")]
    [InlineData("a..b@example.com")]
    [InlineData("a@localhost")]
    public void A_malformed_recipient_address_is_refused(string? input) =>
        RecipientAddress.TryParse(input, out _).ShouldBeFalse();

    [Fact]
    public void A_list_addresses_are_its_address_then_its_aliases()
    {
        GroupMail mail = new(Address("finance@acme.example"), SenderPolicy.Members) { Aliases = [Address("accounts@acme.example")] };
        mail.Addresses.Select(static a => a.Value).ShouldBe(["finance@acme.example", "accounts@acme.example"]);
    }

    [Theory]
    [InlineData(SenderPolicy.Members, true)]
    [InlineData(SenderPolicy.Organization, true)]
    [InlineData(SenderPolicy.Anyone, false)]
    public void Outside_recipients_need_a_list_closed_to_outside_senders(SenderPolicy senders, bool valid)
    {
        GroupMail mail = new(Address("finance@acme.example"), senders) { ExternalRecipients = [Recipient("accountant@example.net")] };
        Result result = mail.Validate();
        result.IsSuccess.ShouldBe(valid);
        if (!valid)
        {
            result.Error!.Kind.ShouldBe(ErrorKind.Validation);
            result.Error.Target.ShouldBe("senders");
        }
    }

    [Fact]
    public void A_list_open_to_anyone_without_outside_recipients_is_valid() =>
        new GroupMail(Address("finance@acme.example"), SenderPolicy.Anyone).Validate().IsSuccess.ShouldBeTrue();

    [Fact]
    public void A_members_rule_lists_the_member_addresses_once_in_order()
    {
        GroupMail mail = new(Address("finance@acme.example"), SenderPolicy.Members) { Aliases = [Address("accounts@acme.example")] };
        SenderRule rule = SenderRule.For(mail, ["Dave@acme.example", "carol@acme.example", "dave@acme.example "], [Domain("acme.example")])!;
        rule.ListAddresses.Select(static a => a.Value).ShouldBe(["finance@acme.example", "accounts@acme.example"]);
        rule.AllowedSenders.ShouldBe(["carol@acme.example", "dave@acme.example"]);
        rule.AllowedDomains.ShouldBeEmpty();
    }

    [Fact]
    public void An_organisation_rule_lists_the_entity_domains()
    {
        GroupMail mail = new(Address("finance@acme.example"), SenderPolicy.Organization);
        SenderRule rule = SenderRule.For(mail, ["carol@acme.example"], [Domain("b.example"), Domain("acme.example"), Domain("b.example")])!;
        rule.AllowedSenders.ShouldBeEmpty();
        rule.AllowedDomains.Select(static d => d.Name).ShouldBe(["acme.example", "b.example"]);
    }

    [Fact]
    public void A_list_open_to_anyone_has_no_rule() =>
        SenderRule.For(new GroupMail(Address("finance@acme.example"), SenderPolicy.Anyone), [], []).ShouldBeNull();

    [Fact]
    public void A_group_starts_without_facets()
    {
        Group group = new("g1", "e1", Key("finance"), "Finance");
        group.Mail.ShouldBeNull();
        group.Files.ShouldBeNull();
        new GroupFiles(TeamFolder: true).TeamFolderId.ShouldBeNull();
    }

    private static GroupKey Key(string value) => GroupKey.TryParse(value, out GroupKey? key) ? key : throw new ArgumentException(value);

    private static MailAddress Address(string value) => MailAddress.TryParse(value, out MailAddress? address) ? address : throw new ArgumentException(value);

    private static RecipientAddress Recipient(string value) => RecipientAddress.TryParse(value, out RecipientAddress? address) ? address : throw new ArgumentException(value);

    private static MailDomain Domain(string value) => MailDomain.TryParse(value, out MailDomain? domain) ? domain : throw new ArgumentException(value);
}
