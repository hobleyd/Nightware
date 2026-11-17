using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Nightware.Hosting.Http;

namespace Nightware.Hosting.Tests.Http;

/// <summary>Trusted-proxy settings and how they reach ASP.NET Core's forwarded-headers options.</summary>
public sealed class ProxyOptionsTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddTrustedProxies(configuration).BuildServiceProvider();
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
    public void A_forward_limit_below_one_is_rejected() =>
        new ProxyOptionsValidator().Validate(null, new ProxyOptions { ForwardLimit = 0 }).Failed.ShouldBeTrue();

    [Fact]
    public void Well_formed_settings_pass_validation()
    {
        var settings = new ProxyOptions();
        settings.KnownProxies.Add("10.9.8.7");
        settings.KnownNetworks.Add("172.31.250.0/24");

        new ProxyOptionsValidator().Validate(null, settings).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void The_registration_binds_the_section_into_forwarded_headers_options()
    {
        using var provider = Build(new()
        {
            ["ForwardedHeaders:KnownProxies:0"] = "10.9.8.7",
            ["ForwardedHeaders:KnownNetworks:0"] = "172.31.250.0/24",
            ["ForwardedHeaders:ForwardLimit"] = "3",
        });

        var forwarded = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        forwarded.KnownProxies.ShouldHaveSingleItem().ToString().ShouldBe("10.9.8.7");
        forwarded.KnownIPNetworks.ShouldHaveSingleItem().ToString().ShouldBe("172.31.250.0/24");
        forwarded.ForwardLimit.ShouldBe(3);
    }

    [Fact]
    public void With_nothing_configured_the_loopback_default_is_kept()
    {
        using var provider = Build([]);

        var forwarded = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        forwarded.KnownProxies.ShouldNotBeEmpty();
        forwarded.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedPrefix).ShouldBeTrue();
    }

    [Fact]
    public void The_registration_validates_the_section()
    {
        using var provider = Build(new() { ["ForwardedHeaders:KnownNetworks:0"] = "garbage" });

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ProxyOptions>>().Value);
    }
}
