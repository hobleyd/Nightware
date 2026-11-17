using Nightware.Domain.Administration;

namespace Nightware.Domain.Tests.Administration;

public sealed class MailAddressingTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData(" Example.COM. ", "example.com")]
    [InlineData("mail.acme.com.au", "mail.acme.com.au")]
    [InlineData("xn--bcher-kva.example", "xn--bcher-kva.example")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    public void A_domain_is_normalised(string input, string expected)
    {
        MailDomain.TryParse(input, out MailDomain? domain).ShouldBeTrue();
        domain!.Name.ShouldBe(expected);
        domain.ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("-bad.example")]
    [InlineData("bad-.example")]
    [InlineData("a..example")]
    [InlineData("under_score.example")]
    [InlineData("1.2.3.4")]
    [InlineData("example.com/path")]
    [InlineData("exa mple.com")]
    public void A_malformed_domain_is_refused(string? input) =>
        MailDomain.TryParse(input, out _).ShouldBeFalse();

    [Fact]
    public void A_label_over_63_characters_is_refused() =>
        MailDomain.TryParse(new string('a', 64) + ".example", out _).ShouldBeFalse();

    [Fact]
    public void Domains_with_the_same_name_are_equal()
    {
        MailDomain.TryParse("A.example", out MailDomain? a).ShouldBeTrue();
        MailDomain.TryParse("a.example", out MailDomain? b).ShouldBeTrue();
        a.ShouldBe(b);
    }

    [Theory]
    [InlineData("Carol.Jones@Acme.Example", "carol.jones", "acme.example")]
    [InlineData(" first_last-2@acme.example ", "first_last-2", "acme.example")]
    public void An_address_is_normalised(string input, string local, string domain)
    {
        MailAddress.TryParse(input, out MailAddress? address).ShouldBeTrue();
        address!.LocalPart.ShouldBe(local);
        address.Domain.Name.ShouldBe(domain);
        address.Value.ShouldBe($"{local}@{domain}");
        address.ToString().ShouldBe(address.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("carol")]
    [InlineData("@acme.example")]
    [InlineData(".carol@acme.example")]
    [InlineData("carol.@acme.example")]
    [InlineData("ca..rol@acme.example")]
    [InlineData("carol+tag@acme.example")]
    [InlineData("car ol@acme.example")]
    [InlineData("carol@localhost")]
    [InlineData("\"carol\"@acme.example")]
    public void A_malformed_or_unusual_address_is_refused(string? input) =>
        MailAddress.TryParse(input, out _).ShouldBeFalse();

    [Fact]
    public void A_local_part_over_64_characters_is_refused() =>
        MailAddress.TryParse(new string('a', 65) + "@acme.example", out _).ShouldBeFalse();

    [Fact]
    public void An_address_can_be_built_from_a_local_part()
    {
        MailDomain.TryParse("acme.example", out MailDomain? domain).ShouldBeTrue();
        MailAddress.TryCreate("Dave", domain!, out MailAddress? address).ShouldBeTrue();
        address!.Value.ShouldBe("dave@acme.example");
        MailAddress.TryCreate("da ve", domain!, out _).ShouldBeFalse();
    }

    [Fact]
    public void Matches_compares_without_case_or_padding()
    {
        MailAddress.TryParse("carol@acme.example", out MailAddress? address).ShouldBeTrue();
        address!.Matches(" Carol@ACME.example ").ShouldBeTrue();
        address.Matches("carol@other.example").ShouldBeFalse();
        address.Matches(null).ShouldBeFalse();
    }
}
