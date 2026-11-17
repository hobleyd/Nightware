using Nightware.Adapters.Cloudflare.Administration;

namespace Nightware.Adapters.Cloudflare.Tests.Administration;

public sealed class CloudflareOptionsValidatorTests
{
    private static string[] Failures(Action<CloudflareOptions> change)
    {
        var options = new CloudflareOptions();
        change(options);
        return [.. new CloudflareOptionsValidator().Validate(null, options).Failures ?? []];
    }

    [Fact]
    public void The_defaults_are_valid() => Failures(static _ => { }).ShouldBeEmpty();

    [Fact]
    public void The_base_url_is_absolute_http_and_ends_with_a_slash()
    {
        Failures(static o => o.ApiBaseUrl = new Uri("/v4/", UriKind.Relative)).ShouldHaveSingleItem().ShouldContain("absolute");
        Failures(static o => o.ApiBaseUrl = new Uri("ftp://api.example/")).ShouldHaveSingleItem().ShouldContain("absolute");
        Failures(static o => o.ApiBaseUrl = new Uri("https://api.cloudflare.com/client/v4")).ShouldHaveSingleItem().ShouldContain("end with '/'");
    }

    [Fact]
    public void The_public_resolver_is_an_absolute_url_with_a_positive_timeout()
    {
        Failures(static o => o.PublicDnsUrl = new Uri("/dns-query", UriKind.Relative)).ShouldHaveSingleItem().ShouldContain("PublicDnsUrl");
        Failures(static o => o.PublicDnsTimeout = TimeSpan.Zero).ShouldHaveSingleItem().ShouldContain("PublicDnsTimeout");
    }

    [Fact]
    public void Timeouts_retries_and_paging_are_bounded()
    {
        Failures(static o => o.AttemptTimeout = TimeSpan.Zero).ShouldHaveSingleItem();
        Failures(static o => o.AttemptTimeout = TimeSpan.FromMinutes(5)).ShouldHaveSingleItem().ShouldContain("cannot exceed");
        Failures(static o => o.MaxRetryAttempts = 11).ShouldHaveSingleItem();
        Failures(static o => o.RecordPageSize = 2).ShouldHaveSingleItem();
        Failures(static o => o.MaxRecords = 100).ShouldHaveSingleItem();
    }
}
