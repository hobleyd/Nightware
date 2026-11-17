using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Nightware.Hosting.Observability;

/// <summary>
/// What the façade must never log — <c>Authorization</c>, tokens, cookies, message bodies, URL secrets — and
/// the framework logging that would otherwise do it.
/// </summary>
/// <remarks>
/// <para>Nothing in the façade logs headers or bodies: HTTP request/response logging
/// (<c>AddHttpLogging</c>) is deliberately not registered, adapters log only status codes and
/// backend error codes, and IdentityModel keeps <c>ShowPII</c> off so token-validation failures
/// never print the token.</para>
/// <para>ASP.NET Core's request-line log (<c>Request starting GET https://…?…</c>, category
/// <see cref="HostingDiagnosticsCategory"/>) prints the full URL including the query string. A
/// self-authorising URL carries a credential in its query, and <c>$search</c>/<c>$filter</c> carry what the
/// user typed. That category is pinned to <see cref="LogLevel.Warning"/> after configuration is applied, so
/// turning <c>Logging:LogLevel:Default</c> down to Debug does not re-enable it; per-request visibility comes
/// from traces and the <c>http.server.request.duration</c> metric, which record the route template and a
/// query-redacted URL instead. Routing's matcher (<see cref="RoutingMatcherCategory"/>), which prints each
/// request path at Debug, is pinned to Information the same way.</para>
/// <para>Paths are otherwise kept — the hosting log scope's <c>RequestPath</c> and the span's <c>url.path</c>
/// carry them — so a self-authorising URL carries its credential in the query (as Graph's own
/// <c>?authtoken=</c> URLs do), never in a path segment. The two path segments that are secrets in themselves
/// (<see cref="UrlRedaction"/>: a share id, a Nextcloud public-share token) are replaced wherever a path is
/// recorded: in every log scope (<see cref="RedactingScopeProvider"/>), in HttpClient's request logs
/// (<see cref="RedactingHttpClientLoggingFilter"/>, which replaces the default logger that redacts only the
/// query) and on exported spans (<c>RedactingSpanProcessor</c> in the host). <c>LogRedactionTests</c> pins the whole
/// pipeline.</para>
/// </remarks>
public static class LogRedaction
{
    public const string HostingDiagnosticsCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";
    public const string RoutingMatcherCategory = "Microsoft.AspNetCore.Routing.Matching";

    public static IServiceCollection AddLogRedaction(this IServiceCollection services)
    {
        // PostConfigure: appended after the configuration's rules, so it wins over an equally specific
        // Logging:LogLevel entry. Only a provider-specific rule (Logging:Console:LogLevel:…) overrides it.
        services.PostConfigure<LoggerFilterOptions>(static options =>
        {
            options.Rules.Add(new LoggerFilterRule(null, HostingDiagnosticsCategory, LogLevel.Warning, null));
            options.Rules.Add(new LoggerFilterRule(null, RoutingMatcherCategory, LogLevel.Information, null));
        });

        // LoggerFactory takes its scope provider from DI when one is registered.
        services.TryAddSingleton<IExternalScopeProvider>(static sp =>
            new RedactingScopeProvider(sp.GetRequiredService<IOptions<LoggerFactoryOptions>>().Value.ActivityTrackingOptions));

        services.ConfigureHttpClientDefaults(static client => client.RemoveAllLoggers());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, RedactingHttpClientLoggingFilter>());
        return services;
    }
}
