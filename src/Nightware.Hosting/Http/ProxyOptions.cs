using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Nightware.Hosting.Http;

/// <summary>
/// Which reverse proxies may set <c>X-Forwarded-For/Proto/Host/Prefix</c> (configuration section
/// <c>ForwardedHeaders</c>).
/// </summary>
/// <remarks>
/// The façade runs behind Caddy, which terminates TLS. Trusting forwarded headers from anyone
/// would let a caller choose the client address the per-IP rate-limit partition and the logs see,
/// and the scheme/host a link is built on when no public base URL is configured. With both lists
/// empty only loopback proxies are trusted (ASP.NET Core's default); the compose stack trusts its
/// own subnet.
/// </remarks>
public sealed class ProxyOptions
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Proxy addresses, e.g. <c>10.0.0.5</c>.</summary>
    public IList<string> KnownProxies { get; } = [];

    /// <summary>Proxy networks in CIDR form, e.g. <c>172.31.250.0/24</c>.</summary>
    public IList<string> KnownNetworks { get; } = [];

    /// <summary>How many proxy hops to unwind; one for a single Caddy in front.</summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>Applies these settings to ASP.NET Core's options; malformed entries were rejected by validation.</summary>
    internal void ApplyTo(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedPrefix;
        options.ForwardLimit = ForwardLimit;

        if (KnownProxies.Count == 0 && KnownNetworks.Count == 0)
        {
            return; // keep the framework default: loopback only
        }

        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in KnownProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        foreach (var network in KnownNetworks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }
    }
}

internal sealed class ProxyOptionsValidator : IValidateOptions<ProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        failures.AddRange(options.KnownProxies
            .Where(static p => !IPAddress.TryParse(p, out _))
            .Select(static p => $"ForwardedHeaders:KnownProxies entry '{p}' is not an IP address."));
        failures.AddRange(options.KnownNetworks
            .Where(static n => !System.Net.IPNetwork.TryParse(n, out _))
            .Select(static n => $"ForwardedHeaders:KnownNetworks entry '{n}' is not a CIDR network."));
        if (options.ForwardLimit < 1)
        {
            failures.Add("ForwardedHeaders:ForwardLimit must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
