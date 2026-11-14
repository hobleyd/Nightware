using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Nightware.Api.Hosting;
using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Hosting;

/// <summary>X-Forwarded-* is honoured from the configured proxies only.</summary>
public sealed class ForwardedHeadersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<TestHost> StartAsync(Dictionary<string, string?>? configuration = null) =>
        TestHost.StartAsync(
            static s => s.AddGraphProbes(static g => g.MapGet("/origin", static (HttpContext http) =>
                $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase} from {http.Connection.RemoteIpAddress}").AllowAnonymous()),
            configuration: configuration);

    private static Task<HttpContext> SendFromAsync(TestHost host, string remoteAddress) =>
        host.App.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
            context.Request.Method = "GET";
            context.Request.Path = "/v1.0/origin";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-Host"] = "graph.example.org";
            context.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        }, Ct);

    private static async Task<string> BodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync(Ct);
    }

    [Fact]
    public async Task A_configured_proxy_network_is_trusted()
    {
        await using var host = await StartAsync(new() { ["ForwardedHeaders:KnownNetworks:0"] = "172.31.250.0/24" });

        var context = await SendFromAsync(host, "172.31.250.7");

        (await BodyAsync(context)).ShouldBe("https://graph.example.org from 203.0.113.9");
    }

    [Fact]
    public async Task A_configured_proxy_address_is_trusted()
    {
        await using var host = await StartAsync(new() { ["ForwardedHeaders:KnownProxies:0"] = "10.9.8.7" });

        var context = await SendFromAsync(host, "10.9.8.7");

        (await BodyAsync(context)).ShouldContain("https://graph.example.org");
    }

    [Fact]
    public async Task Any_other_sender_cannot_spoof_scheme_host_or_client_address()
    {
        await using var host = await StartAsync(new() { ["ForwardedHeaders:KnownNetworks:0"] = "172.31.250.0/24" });

        var context = await SendFromAsync(host, "198.51.100.20");

        var body = await BodyAsync(context);
        body.ShouldNotContain("graph.example.org");
        body.ShouldEndWith("from 198.51.100.20");
    }

    [Fact]
    public async Task With_nothing_configured_only_loopback_is_trusted()
    {
        await using var host = await StartAsync();

        (await BodyAsync(await SendFromAsync(host, "127.0.0.1"))).ShouldContain("https://graph.example.org");
        (await BodyAsync(await SendFromAsync(host, "172.31.250.7"))).ShouldNotContain("graph.example.org");
    }

    [Fact]
    public void Settings_replace_the_framework_defaults()
    {
        var settings = new ProxyOptions { ForwardLimit = 2 };
        settings.KnownNetworks.Add("10.0.0.0/8");
        var options = new ForwardedHeadersOptions();

        settings.ApplyTo(options);

        options.KnownIPNetworks.ShouldHaveSingleItem().ToString().ShouldBe("10.0.0.0/8");
        options.KnownProxies.ShouldBeEmpty();
        options.ForwardLimit.ShouldBe(2);
        options.ForwardedHeaders.ShouldBe(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedPrefix);
    }

    [Theory]
    [InlineData("KnownProxies", "not-an-ip")]
    [InlineData("KnownNetworks", "10.0.0.0/99")]
    [InlineData("KnownNetworks", "10.0.0.1")]
    public void Malformed_proxies_are_rejected(string list, string value)
    {
        var settings = new ProxyOptions();
        (list == "KnownProxies" ? settings.KnownProxies : settings.KnownNetworks).Add(value);

        new ProxyOptionsValidator().Validate(null, settings).Failed.ShouldBeTrue();
    }

    [Fact]
    public async Task Malformed_proxies_fail_the_host_at_start() =>
        await Should.ThrowAsync<OptionsValidationException>(() =>
            StartAsync(new() { ["ForwardedHeaders:KnownNetworks:0"] = "garbage" }));
}
