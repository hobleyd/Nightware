using Microsoft.Extensions.Options;
using Nightware.Adapters.Dav.Core;

namespace Nightware.Adapters.Dav.Tests.Core;

public sealed class DavPathsTests
{
    private static DavPaths Paths(string baseUrl) =>
        new(Options.Create(new DavOptions { BaseUrl = new Uri(baseUrl) }));

    [Fact]
    public void Root_includes_the_nextcloud_sub_path()
    {
        var paths = Paths("https://cloud.example.com/nextcloud/");
        paths.RootPath.ShouldBe("/nextcloud/remote.php/dav/");
        paths.OcsRootPath.ShouldBe("/nextcloud/ocs/v2.php/");
        paths.Origin.ShouldBe(new Uri("https://cloud.example.com/"));
        paths.RootUri.ShouldBe(new Uri("https://cloud.example.com/nextcloud/remote.php/dav/"));
    }

    [Theory]
    [InlineData("calendars/alice/personal/", "/nextcloud/remote.php/dav/calendars/alice/personal/")]
    [InlineData("/nextcloud/remote.php/dav/calendars/alice/x.ics", "/nextcloud/remote.php/dav/calendars/alice/x.ics")]
    [InlineData("https://cloud.example.com/nextcloud/remote.php/dav/principals/users/alice/", "/nextcloud/remote.php/dav/principals/users/alice/")]
    [InlineData("/nextcloud/remote.php/dav/", "/nextcloud/remote.php/dav/")]
    public void Resolves_every_href_form(string href, string expected) =>
        Paths("https://cloud.example.com/nextcloud").ToAbsolutePath(href).ShouldBe(expected);

    [Theory]
    [InlineData("/remote.php/dav/calendars/alice/x.ics")]
    [InlineData("/nextcloud/remote.php/dav/../../index.php")]
    [InlineData("/nextcloud/remote.php/dav/calendars/%2e%2e/x")]
    [InlineData("https://evil.example.com/nextcloud/remote.php/dav/x")]
    [InlineData("//evil.example.com/x")]
    [InlineData("../x")]
    [InlineData("/nextcloud/remote.php/davx/y")]
    [InlineData("")]
    public void Refuses_hrefs_outside_the_dav_root(string href) =>
        Paths("https://cloud.example.com/nextcloud/").ToAbsolutePath(href).ShouldBeNull();

    [Fact]
    public void Relative_strips_the_root()
    {
        var paths = Paths("https://cloud.example.com/");
        paths.ToRelative("/remote.php/dav/calendars/alice/x.ics").ShouldBe("calendars/alice/x.ics");
        paths.ToRelative("/remote.php/dav/").ShouldBeNull();
        paths.ToRequestUri("calendars/alice/").ShouldBe(new Uri("https://cloud.example.com/remote.php/dav/calendars/alice/"));
        paths.ToRequestUri("../x").ShouldBeNull();
    }
}

public sealed class DavOptionsValidatorTests
{
    private static readonly DavOptionsValidator Validator = new();

    [Fact]
    public void Defaults_with_a_base_url_are_valid() =>
        Validator.Validate(null, new DavOptions { BaseUrl = new Uri("https://cloud.example.com/") }).Succeeded.ShouldBeTrue();

    [Fact]
    public void Missing_base_url_fails() =>
        Validator.Validate(null, new DavOptions()).Failed.ShouldBeTrue();

    [Theory]
    [InlineData("ftp://cloud.example.com/")]
    [InlineData("https://cloud.example.com/?x=1")]
    public void Bad_base_urls_fail(string url) =>
        Validator.Validate(null, new DavOptions { BaseUrl = new Uri(url) }).Failed.ShouldBeTrue();

    [Fact]
    public void Relative_paths_and_bad_timeouts_fail()
    {
        var result = Validator.Validate(null, new DavOptions
        {
            BaseUrl = new Uri("https://cloud.example.com/"),
            DavRoot = "remote.php/dav",
            OcsRoot = "/ocs/../x",
            AttemptTimeout = TimeSpan.FromSeconds(10),
            TotalTimeout = TimeSpan.FromSeconds(5),
            HealthCheckTimeout = TimeSpan.Zero,
            MaxRetryAttempts = 11,
        });
        result.Failed.ShouldBeTrue();
        result.Failures!.Count().ShouldBe(5);
    }
}
