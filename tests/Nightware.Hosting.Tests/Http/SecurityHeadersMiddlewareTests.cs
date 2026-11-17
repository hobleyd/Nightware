using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Hosting.Http;

namespace Nightware.Hosting.Tests.Http;

/// <summary>
/// The middleware on a bare host: the default policy is the Graph façade's (byte for byte), and a host
/// can set its own Content-Security-Policy in code or configuration.
/// </summary>
public sealed class SecurityHeadersMiddlewareTests
{
    private const string GraphPolicy = "default-src 'none'; frame-ancestors 'none'; sandbox";
    private const string PagePolicy = "default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<WebApplication> StartAsync(
        Dictionary<string, string?>? configuration = null, Action<SecurityHeadersOptions>? defaults = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(configuration ?? []);
        builder.Services.AddSecurityHeaders(builder.Configuration, defaults);
        var app = builder.Build();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.MapGet("/probe", static () => "ok");
        app.MapGet("/cached", static (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "private, max-age=60";
            return "ok";
        });
        await app.StartAsync(Ct);
        return app;
    }

    [Fact]
    public void The_default_policy_is_the_graph_facades() =>
        new SecurityHeadersOptions().ContentSecurityPolicy.ShouldBe(GraphPolicy);

    [Fact]
    public async Task Every_response_carries_the_hardening_headers()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync("/probe", Ct);

        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        response.Headers.GetValues("X-Frame-Options").Single().ShouldBe("DENY");
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldBe(GraphPolicy);
        response.Headers.GetValues("Referrer-Policy").Single().ShouldBe("no-referrer");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task A_host_can_set_its_own_policy_in_code()
    {
        await using var app = await StartAsync(defaults: static o => o.ContentSecurityPolicy = PagePolicy);

        var response = await app.GetTestClient().GetAsync("/probe", Ct);

        response.Headers.GetValues("Content-Security-Policy").Single().ShouldBe(PagePolicy);
        response.Headers.GetValues("X-Frame-Options").Single().ShouldBe("DENY");
    }

    [Fact]
    public async Task Configuration_overrides_the_hosts_default_policy()
    {
        await using var app = await StartAsync(
            new() { ["SecurityHeaders:ContentSecurityPolicy"] = "default-src 'self'" },
            static o => o.ContentSecurityPolicy = PagePolicy);

        var response = await app.GetTestClient().GetAsync("/probe", Ct);

        response.Headers.GetValues("Content-Security-Policy").Single().ShouldBe("default-src 'self'");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_policy_fails_the_host_at_start(string policy) =>
        await Should.ThrowAsync<OptionsValidationException>(() =>
            StartAsync(defaults: o => o.ContentSecurityPolicy = policy));

    [Fact]
    public async Task An_endpoints_own_cache_control_is_kept()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync("/cached", Ct);

        response.Headers.CacheControl!.Private.ShouldBeTrue();
        response.Headers.CacheControl.NoStore.ShouldBeFalse();
    }

    [Fact]
    public async Task Hsts_is_sent_only_over_https()
    {
        await using var app = await StartAsync();
        var https = app.GetTestClient();
        https.BaseAddress = new Uri("https://host.test.example/");

        var secure = await https.GetAsync("/probe", Ct);
        var plain = await app.GetTestClient().GetAsync("/probe", Ct);

        secure.Headers.GetValues("Strict-Transport-Security").Single().ShouldBe("max-age=31536000");
        plain.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
    }

    [Fact]
    public async Task Hsts_can_be_left_to_the_proxy()
    {
        await using var app = await StartAsync(new() { ["SecurityHeaders:HstsMaxAge"] = "00:00:00" });
        var https = app.GetTestClient();
        https.BaseAddress = new Uri("https://host.test.example/");

        var response = await https.GetAsync("/probe", Ct);

        response.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
    }

    [Fact]
    public void Options_without_the_registration_still_default_to_the_graph_policy()
    {
        using var provider = new ServiceCollection().AddOptions().BuildServiceProvider();

        provider.GetRequiredService<IOptions<SecurityHeadersOptions>>().Value.ContentSecurityPolicy.ShouldBe(GraphPolicy);
    }
}
