using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

using Nightware.Api.Hosting;

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
    [InlineData("/v1.0/shares/u!aGk/driveItem/$value", "u!aGk")]
    [InlineData("/beta/shares/u!aGk/driveItem/$value", "u!aGk")]
    [InlineData("/v1.0/Shares/u!aGk/DriveItem/$value", "u!aGk")]
    public void Parses_its_own_paths(string path, string shareId)
    {
        DownloadUrl.TryParse(new PathString(path), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(shareId);
    }

    [Theory]
    [InlineData("/v1.0/shares/u!aGk/driveItem/content")]
    [InlineData("/v1.0/shares/u!aGk/driveItem")]
    [InlineData("/v1.0/shares//driveItem/$value")]
    [InlineData("/v1.0/shares/u!aGk/driveItem/$value/extra")]
    [InlineData("/v1.0/shares/u!aGk/driveItem/$VALUE")]
    [InlineData("/v2.0/shares/u!aGk/driveItem/$value")]
    [InlineData("/shares/u!aGk/driveItem/$value")]
    public void Ignores_other_paths(string path) =>
        DownloadUrl.TryParse(new PathString(path), out _).ShouldBeFalse();

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
