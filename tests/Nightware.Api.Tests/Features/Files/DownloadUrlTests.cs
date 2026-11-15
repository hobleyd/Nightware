using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

using Nightware.Api.Hosting;
using Nightware.Api.Tests.Auth;

using Nightware.Api.Features.Files.Download;
using Nightware.Domain.Files;

namespace Nightware.Api.Tests.Features.Files;

public sealed class DownloadUrlTests
{
    [Fact]
    public void Builds_a_url_under_the_version_root_with_the_format_and_token()
    {
        DownloadUrl.Build("https://graph.example.com/v1.0", new DownloadUrlBinding("u!aGk", ContentFormat.Pdf), "sealed+/=")
            .ShouldBe("https://graph.example.com/v1.0/shares/u%21aGk/driveItem/$value?format=pdf&authtoken=sealed%2B%2F%3D");
        DownloadUrl.Build("/beta", new DownloadUrlBinding("u!aGk", ContentFormat.Original), "s")
            .ShouldBe("/beta/shares/u%21aGk/driveItem/$value?authtoken=s");
    }

    [Theory]
    [InlineData("", ContentFormat.Original)]
    [InlineData("?format=pdf", ContentFormat.Pdf)]
    [InlineData("?FORMAT=PDF", ContentFormat.Pdf)]
    public void Binds_the_matched_share_and_format(string query, ContentFormat format)
    {
        var routed = SelfAuthorisingUrlCredentialSourceTests.Routed("GET", DownloadUrl.Kind, query, ("shareId", "u!aGk"));

        DownloadUrl.Kind.BindingOf(routed.Request).ShouldBe(new DownloadUrlBinding("u!aGk", format).BoundTo);
    }

    [Theory]
    [InlineData("?format=html")]
    [InlineData("?format=pdf&format=pdf")]
    public void A_bad_format_binds_nothing(string query) =>
        DownloadUrl.Kind.BindingOf(SelfAuthorisingUrlCredentialSourceTests.Routed("GET", DownloadUrl.Kind, query, ("shareId", "u!aGk")).Request).ShouldBeNull();

    [Fact]
    public void A_download_url_is_get_only_and_keeps_its_key_purpose()
    {
        DownloadUrl.Kind.Method.ShouldBe("GET");
        DownloadUrl.Kind.UrlOnly.ShouldBeFalse("a bearer may download what /content would redirect it to");
        DownloadUrl.Kind.Purpose.ShouldBe("nightware/share-download-url/v1", "changing the purpose would break URLs already handed out");
        DownloadUrl.Kind.Lifetime.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void The_download_route_gets_the_transfer_budget()
    {
        var options = new RequestBudgetOptions { Default = TimeSpan.FromSeconds(1), Transfer = TimeSpan.FromSeconds(9) };
        var context = new DefaultHttpContext();
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/v1.0" + DownloadUrl.RoutePattern), 0, null, null));

        RequestBudgetMiddleware.BudgetFor(context, options).ShouldBe(TimeSpan.FromSeconds(9));
    }

    [Theory]
    [InlineData(null, ContentFormat.Original)]
    [InlineData("", ContentFormat.Original)]
    [InlineData("pdf", ContentFormat.Pdf)]
    [InlineData("PDF", ContentFormat.Pdf)]
    public void Reads_the_supported_formats(string? value, ContentFormat expected)
    {
        DownloadUrl.TryParseFormat(value, out var format).ShouldBeTrue();
        format.ShouldBe(expected);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("glb")]
    public void Refuses_other_formats(string value) =>
        DownloadUrl.TryParseFormat(value, out _).ShouldBeFalse();
}
