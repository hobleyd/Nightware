using Nightware.Adapters.Dav.Files;

namespace Nightware.Adapters.Dav.Tests.Files;

public sealed class NextcloudLinkTests
{
    [Theory]
    [InlineData("s/jeKXiPKYQXyWQAT", "jeKXiPKYQXyWQAT")]
    [InlineData("s/jeKXiPKYQXyWQAT/", "jeKXiPKYQXyWQAT")]
    [InlineData("index.php/s/jeKXiPKYQXyWQAT", "jeKXiPKYQXyWQAT")]
    [InlineData("s/custom-token_1+x", "custom-token_1+x")]
    public void Public_shares_are_recognised(string sitePath, string token)
    {
        NextcloudLink.TryParse(sitePath, out var link).ShouldBeTrue();
        link.ShouldBe(new NextcloudLink.PublicShare(token));
    }

    [Theory]
    [InlineData("f/385", 385)]
    [InlineData("f/385/", 385)]
    [InlineData("index.php/f/385", 385)]
    public void Internal_links_are_recognised(string sitePath, long fileId)
    {
        NextcloudLink.TryParse(sitePath, out var link).ShouldBeTrue();
        link.ShouldBe(new NextcloudLink.InternalFile(fileId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s/")]
    [InlineData("s/abc/download")]
    [InlineData("s/abc/download/file.txt")]
    [InlineData("s/../etc")]
    [InlineData("s/a%2Fb")]
    [InlineData("f/0")]
    [InlineData("f/abc")]
    [InlineData("f/-1")]
    [InlineData("f/1234567890123456789")]
    [InlineData("apps/files/files/385")]
    [InlineData("apps/files/?dir=/")]
    [InlineData("remote.php/dav/files/alice/x.docx")]
    [InlineData("index.php/apps/files/")]
    [InlineData("/s/abc")]
    public void Anything_else_is_not_a_document_link(string? sitePath) =>
        NextcloudLink.TryParse(sitePath, out _).ShouldBeFalse();
}
