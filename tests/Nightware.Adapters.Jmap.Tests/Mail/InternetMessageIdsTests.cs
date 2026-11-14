using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Mail;

public sealed class InternetMessageIdsTests
{
    private static readonly Guid Fixed = Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    [Fact]
    public void Mint_uses_a_uuid_and_the_mailbox_domain()
    {
        InternetMessageIds.Mint(new MailboxRef("Alice@Nightware.Localhost"), Fixed)
            .ShouldBe("0193a1b2c3d47e5f8a9b0c1d2e3f4a5b@nightware.localhost");
    }

    [Theory]
    [InlineData("")]
    [InlineData("alice")]
    [InlineData("alice@")]
    [InlineData("alice@bad domain")]
    public void Mint_falls_back_to_an_invalid_domain(string address)
    {
        InternetMessageIds.Mint(new MailboxRef(address), Fixed).ShouldEndWith("@nightware.invalid");
    }

    [Fact]
    public void Minted_ids_are_unique_and_usable_as_draft_ids()
    {
        var a = InternetMessageIds.Mint(new MailboxRef("a@x.org"));
        var b = InternetMessageIds.Mint(new MailboxRef("a@x.org"));

        a.ShouldNotBe(b);
        MailItemId.IsValidMessageId(a).ShouldBeTrue();
        MailItemId.Parse(MailItemId.ForDraft(a).ToMessageId()).Value.Value.ShouldBe(a);
    }

    [Theory]
    [InlineData("<a@b>", "a@b")]
    [InlineData(" <a@b> ", "a@b")]
    [InlineData("a@b", "a@b")]
    [InlineData("<a@b", "<a@b")]
    public void StripBrackets_removes_surrounding_brackets(string input, string expected)
    {
        InternetMessageIds.StripBrackets(input).ShouldBe(expected);
    }

    [Fact]
    public void ToHeaderValue_adds_brackets_once()
    {
        InternetMessageIds.ToHeaderValue("a@b").ShouldBe("<a@b>");
        InternetMessageIds.ToHeaderValue("<a@b>").ShouldBe("<a@b>");
    }
}
