using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

using Nightware.Hosting.Http;

namespace Nightware.Hosting;

/// <summary>Registrations every Nightware host makes the same way.</summary>
public static class HostingDefaultsServiceCollectionExtensions
{
    /// <summary>
    /// Trusted reverse proxies (<see cref="ProxyOptions"/>, section <c>ForwardedHeaders</c>, validated on
    /// start) applied to ASP.NET Core's <see cref="ForwardedHeadersOptions"/>; the host still calls
    /// <c>UseForwardedHeaders()</c> first in its pipeline.
    /// </summary>
    public static IServiceCollection AddTrustedProxies(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<ProxyOptions>().Bind(configuration.GetSection(ProxyOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ProxyOptions>, ProxyOptionsValidator>());
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ProxyOptions>>(static (forwarded, proxies) => proxies.Value.ApplyTo(forwarded));
        return services;
    }

    /// <summary>
    /// <see cref="SecurityHeadersOptions"/> for <see cref="SecurityHeadersMiddleware"/>: <paramref name="defaults"/>
    /// first (a host's own Content-Security-Policy), then the <c>SecurityHeaders</c> section over them.
    /// </summary>
    public static IServiceCollection AddSecurityHeaders(
        this IServiceCollection services, IConfiguration configuration, Action<SecurityHeadersOptions>? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var builder = services.AddOptions<SecurityHeadersOptions>();
        if (defaults is not null)
        {
            builder.Configure(defaults);
        }

        builder.Bind(configuration.GetSection(SecurityHeadersOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SecurityHeadersOptions>, SecurityHeadersOptionsValidator>());
        return services;
    }

    /// <summary>
    /// Cookies off on the primary handler of every <see cref="IHttpClientFactory"/> client in the process
    /// (<see cref="CookielessHttpClientsFilter"/>).
    /// </summary>
    public static IServiceCollection AddCookielessHttpClients(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, CookielessHttpClientsFilter>());
        return services;
    }
}
