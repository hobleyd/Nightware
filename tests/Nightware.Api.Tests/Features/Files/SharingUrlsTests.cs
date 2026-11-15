using System.Buffers.Text;
using System.Text;

using Nightware.Api.Features.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Files;

public sealed class SharingUrlsTests
{
    private static readonly string[] Sites = ["https://cloud.example.com", "https://files.example.com/nextcloud/"];

    /// <summary>Exactly NightMail's <c>GraphDriveDatasourceImpl.encodeSharingUrl</c>.</summary>
    internal static string Encode(string url) => "u!" + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(url));

    [Theory]
    [InlineData("https://cloud.example.com/s/AbCdEfGh", "s/AbCdEfGh")]
    [InlineData("https://CLOUD.example.com/index.php/f/385", "index.php/f/385")]
    [InlineData("https://cloud.example.com:443/s/x?dir=/", "s/x")]
    [InlineData("https://files.example.com/nextcloud/s/AbC/", "s/AbC/")]
    [InlineData("https://cloud.example.com/s/Ab%20C", "s/Ab%20C")]
    public void A_link_below_a_file_site_resolves_to_its_site_path(string url, string sitePath)
    {
        var link = SharingUrls.Resolve(Encode(url), Sites).Value;

        link.SitePath.ShouldBe(sitePath);
        link.Url.ShouldBe(new Uri(url));
    }

    [Theory]
    [InlineData("https://evil.example.com/s/AbCdEfGh")]
    [InlineData("http://cloud.example.com/s/AbCdEfGh")]
    [InlineData("https://cloud.example.com:8443/s/AbCdEfGh")]
    [InlineData("https://cloud.example.com.evil.test/s/AbCdEfGh")]
    [InlineData("https://files.example.com/nextcloudish/s/AbC")]
    [InlineData("https://files.example.com/s/AbC")]
    [InlineData("https://cloud.example.com/")]
    [InlineData("https://cloud.example.com")]
    public void A_link_anywhere_else_is_not_found(string url) =>
        SharingUrls.Resolve(Encode(url), Sites).Error!.Kind.ShouldBe(ErrorKind.NotFound);

    [Fact]
    public void Without_file_sites_every_link_is_not_found()
    {
        SharingUrls.Resolve(Encode("https://cloud.example.com/s/AbC"), null).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        SharingUrls.Resolve(Encode("https://cloud.example.com/s/AbC"), []).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public void Unparseable_site_entries_are_skipped() =>
        SharingUrls.Resolve(Encode("https://cloud.example.com/s/AbC"), ["not a url", "https://cloud.example.com"]).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("u!")]
    [InlineData("s!aHR0cHM6Ly9jbG91ZC5leGFtcGxlLmNvbS9zL0FiQw")]
    [InlineData("u!***")]
    [InlineData("u!/w")]
    public void A_malformed_share_id_is_a_validation_error(string shareId) =>
        SharingUrls.Resolve(shareId, Sites).Error!.Kind.ShouldBe(ErrorKind.Validation);

    [Theory]
    [InlineData("ftp://cloud.example.com/s/AbC")]
    [InlineData("mailto:a@example.com")]
    [InlineData("/s/AbC")]
    [InlineData("https://user:pw@cloud.example.com/s/AbC")]
    public void Only_absolute_http_urls_without_credentials_decode(string url) =>
        SharingUrls.TryDecode(Encode(url), out _).ShouldBeFalse();

    [Fact]
    public void Padded_and_standard_base64_are_tolerated()
    {
        var standard = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes("https://cloud.example.com/s/Ab?>"));

        SharingUrls.TryDecode(standard, out var url).ShouldBeTrue();
        url.AbsoluteUri.ShouldBe("https://cloud.example.com/s/Ab?%3E");
    }

    [Fact]
    public void Invalid_utf8_does_not_decode() =>
        SharingUrls.TryDecode("u!" + Base64Url.EncodeToString([0xff, 0xfe, 0xfd]), out _).ShouldBeFalse();

    [Fact]
    public void An_oversized_share_id_does_not_decode() =>
        SharingUrls.TryDecode(Encode("https://cloud.example.com/s/" + new string('a', 4000)), out _).ShouldBeFalse();

    [Fact]
    public void The_conformance_fixture_link_decodes() =>
        SharingUrls.Resolve("u!aHR0cHM6Ly9maWxlcy5leGFtcGxlLmNvbS9zL0FiQ2RFZkdo", ["https://files.example.com"]).Value.SitePath.ShouldBe("s/AbCdEfGh");
}
