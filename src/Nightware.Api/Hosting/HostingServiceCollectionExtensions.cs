using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Hosting;

namespace Nightware.Api.Hosting;

public static class HostingServiceCollectionExtensions
{
    /// <summary>Kestrel's configuration section; its <c>Limits</c> are bound here (see appsettings.json).</summary>
    public const string KestrelSectionName = "Kestrel";

    /// <summary>
    /// HTTP-level hardening: trusted proxies, CORS, security headers, body-size limits, request
    /// budgets and Kestrel limits — each from its own configuration section, validated on start.
    /// </summary>
    public static IServiceCollection AddHttpHardening(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddTrustedProxies(configuration);
        services.AddSecurityHeaders(configuration);
        AddValidated<RequestLimitOptions, RequestLimitOptionsValidator>(services, configuration, RequestLimitOptions.SectionName);
        AddValidated<RequestBudgetOptions, RequestBudgetOptionsValidator>(services, configuration, RequestBudgetOptions.SectionName);

        // Header and connection limits come straight from Kestrel's own section; the body limit is
        // RequestLimitOptions (enforced per request, whatever the server). No "Server: Kestrel"
        // banner unless configuration turns it back on.
        services.Configure<KestrelServerOptions>(static kestrel => kestrel.AddServerHeader = false);
        services.Configure<KestrelServerOptions>(configuration.GetSection(KestrelSectionName));
        services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<RequestLimitOptions>>(static (kestrel, limits) =>
                kestrel.Limits.MaxRequestBodySize = Math.Max(limits.Value.MaxRequestBodyBytes, limits.Value.MaxLargeUploadBytes));

        // Pooled primary handlers are shared across users: no client may keep cookies.
        services.AddCookielessHttpClients();

        services.AddGraphCors(configuration);
        return services;
    }

    private static void AddValidated<TOptions, TValidator>(IServiceCollection services, IConfiguration configuration, string section)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddOptions<TOptions>().Bind(configuration.GetSection(section)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TOptions>, TValidator>());
    }
}
