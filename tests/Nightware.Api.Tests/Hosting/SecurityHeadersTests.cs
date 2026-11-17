using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Hosting;

/// <summary>Every response carries the "render nothing, cache nothing" headers.</summary>
public sealed class SecurityHeadersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<TestHost> StartAsync(Dictionary<string, string?>? configuration = null) =>
        TestHost.StartAsync(
            static s => s.AddGraphProbes(static g =>
            {
                g.MapGet("/probe", static () => Results.Ok());
                g.MapGet("/cached", static (HttpContext http) =>
                {
                    http.Response.Headers.CacheControl = "private, max-age=60";
                    return Results.Ok();
                });
            }),
            configuration: configuration);

    [Theory]
    [InlineData("/v1.0/probe", true)]
    [InlineData("/v1.0/probe", false)]
    [InlineData("/v1.0/no/such/route", true)]
    [InlineData("/.well-known/nightware", false)]
    public async Task Responses_carry_the_hardening_headers(string path, bool authorized)
    {
        await using var host = await StartAsync();
        var client = authorized ? host.AuthorizedClient() : host.Client;

        var response = await client.GetAsync(path, Ct);

        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        response.Headers.GetValues("X-Frame-Options").Single().ShouldBe("DENY");
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldBe("default-src 'none'; frame-ancestors 'none'; sandbox");
        response.Headers.GetValues("Referrer-Policy").Single().ShouldBe("no-referrer");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task An_endpoints_own_cache_control_is_kept()
    {
        await using var host = await StartAsync();

        var response = await host.AuthorizedClient().GetAsync("/v1.0/cached", Ct);

        response.Headers.CacheControl!.Private.ShouldBeTrue();
        response.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromSeconds(60));
        response.Headers.CacheControl.NoStore.ShouldBeFalse();
    }

    [Fact]
    public async Task Hsts_is_sent_only_over_https()
    {
        await using var host = await StartAsync();
        var https = host.App.GetTestClient();
        https.BaseAddress = new Uri("https://graph.test.example/");

        var secure = await https.GetAsync("/.well-known/nightware", Ct);
        var plain = await host.Client.GetAsync("/.well-known/nightware", Ct);

        secure.Headers.GetValues("Strict-Transport-Security").Single().ShouldBe("max-age=31536000");
        plain.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
    }

    [Fact]
    public async Task Hsts_can_be_left_to_the_proxy()
    {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["SecurityHeaders:HstsMaxAge"] = "00:00:00" });
        var https = host.App.GetTestClient();
        https.BaseAddress = new Uri("https://graph.test.example/");

        var response = await https.GetAsync("/.well-known/nightware", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
    }
}
