using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Nightware.Api.Features.Files.Download;
using Nightware.Api.Graph.Json;
using Nightware.Domain.Files;

namespace Nightware.Api.Tests.Features.Files;

public sealed class DownloadUrlCredentialSourceTests
{
    private const string ShareId = "u!aGk";
    private const string Path = "/v1.0/shares/u!aGk/driveItem/download";

    private readonly DownloadUrlProtector _protector = new(Options.Create(new DeltaLinkOptions { SigningKey = "k" }), TimeProvider.System);

    private string Sealed(ContentFormat format = ContentFormat.Pdf) =>
        _protector.Seal(new DownloadTicket("the-token", DateTimeOffset.UtcNow.AddMinutes(5)), new DownloadUrlBinding(ShareId, format));

    private bool Resolve(string method, string path, string query, out string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return new DownloadUrlCredentialSource(_protector).TryResolve(context.Request, out token);
    }

    [Fact]
    public void Recovers_the_token_for_a_get_of_its_own_share_and_format()
    {
        Resolve("GET", Path, "?format=pdf&authtoken=" + Sealed(), out var token).ShouldBeTrue();
        token.ShouldBe("the-token");
        Resolve("GET", Path, "?authtoken=" + Sealed(ContentFormat.Original), out _).ShouldBeTrue();
    }

    [Fact]
    public void A_different_format_gets_nothing()
    {
        Resolve("GET", Path, "?authtoken=" + Sealed(), out _).ShouldBeFalse();
        Resolve("GET", Path, "?format=pdf&format=pdf&authtoken=" + Sealed(), out _).ShouldBeFalse();
        Resolve("GET", Path, "?format=html&authtoken=" + Sealed(), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public void Other_methods_get_nothing(string method) =>
        Resolve(method, Path, "?format=pdf&authtoken=" + Sealed(), out _).ShouldBeFalse();

    [Fact]
    public void Other_paths_get_nothing()
    {
        Resolve("GET", "/v1.0/shares/u!aGk/driveItem/content", "?format=pdf&authtoken=" + Sealed(), out _).ShouldBeFalse();
        Resolve("GET", "/v1.0/shares/u!b3RoZXI/driveItem/download", "?format=pdf&authtoken=" + Sealed(), out _).ShouldBeFalse();
        Resolve("GET", "/v1.0/me/messages", "?format=pdf&authtoken=" + Sealed(), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("?format=pdf")]
    [InlineData("?format=pdf&authtoken=")]
    public void Requests_without_a_token_get_nothing(string query) =>
        Resolve("GET", Path, query, out _).ShouldBeFalse();

    [Fact]
    public void Two_tokens_are_refused()
    {
        var value = Sealed();

        Resolve("GET", Path, $"?format=pdf&authtoken={value}&authtoken={value}", out _).ShouldBeFalse();
    }
}
