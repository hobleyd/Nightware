using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nightware.Api.Observability;

/// <summary>
/// What the façade must never log — <c>Authorization</c>, tokens, cookies, message bodies — and the
/// one framework log that would otherwise do it.
/// </summary>
/// <remarks>
/// <para>Nothing in the façade logs headers or bodies: HTTP request/response logging
/// (<c>AddHttpLogging</c>) is deliberately not registered, adapters log only status codes and
/// backend error codes, and IdentityModel keeps <c>ShowPII</c> off so token-validation failures
/// never print the token.</para>
/// <para>The exception is ASP.NET Core's request-line log (<c>Request starting GET https://…?…</c>,
/// category <see cref="HostingDiagnosticsCategory"/>), which prints the full URL including the
/// query string. A self-authorising URL (a To Do upload session) carries a credential in its URL,
/// and <c>$search</c>/<c>$filter</c> carry what the user typed. That category is pinned to
/// <see cref="LogLevel.Warning"/> after configuration is applied, so turning
/// <c>Logging:LogLevel:Default</c> down to Debug does not re-enable it; per-request visibility comes
/// from traces and the <c>http.server.request.duration</c> metric, which record the route template
/// and a query-redacted URL instead. <c>LogRedactionTests</c> pins the whole pipeline.</para>
/// <para>Paths are <b>not</b> redacted: the framework's hosting log scope (<c>RequestPath</c>) and
/// the span's <c>url.path</c> carry them verbatim. A self-authorising URL must therefore carry its
/// credential in the query string (as Graph's own <c>?authtoken=</c> upload URLs do), never in a
/// path segment.</para>
/// </remarks>
public static class LogRedaction
{
    public const string HostingDiagnosticsCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    public static IServiceCollection AddLogRedaction(this IServiceCollection services)
    {
        // PostConfigure: appended after the configuration's rules, so it wins over an equally specific
        // Logging:LogLevel entry. Only a provider-specific rule (Logging:Console:LogLevel:…) overrides it.
        services.PostConfigure<LoggerFilterOptions>(static options =>
            options.Rules.Add(new LoggerFilterRule(null, HostingDiagnosticsCategory, LogLevel.Warning, null)));
        return services;
    }
}
