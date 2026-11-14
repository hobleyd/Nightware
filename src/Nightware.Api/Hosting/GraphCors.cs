using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using Nightware.Api.Graph;

namespace Nightware.Api.Hosting;

/// <summary>Origins allowed to call the façade from a browser (configuration section <c>Cors</c>).</summary>
public sealed class CorsSettings
{
    public const string SectionName = "Cors";

    /// <summary>
    /// Exact origins (<c>scheme://host[:port]</c>), e.g. the NightMail web build's
    /// <c>https://mail.example.com</c>. Empty (the default) means no cross-origin access at all.
    /// </summary>
    public IList<string> AllowedOrigins { get; } = [];
}

/// <summary>
/// The one CORS policy: the NightMail web build may call discovery and the Graph routes with a
/// bearer token. It never applies to <c>/healthz</c>.
/// </summary>
/// <remarks>
/// No credentials mode: the web client sends <c>Authorization</c>, never cookies. The exposed headers
/// are the ones the client reads — without <c>Retry-After</c> exposed, a browser hides it and the
/// client falls back to exponential back-off on 429/503.
/// </remarks>
public static class GraphCors
{
    public const string PolicyName = "nightmail-web";

    public static readonly IReadOnlyList<string> AllowedMethods = ["GET", "POST", "PATCH", "PUT", "DELETE"];

    public static readonly IReadOnlyList<string> AllowedHeaders =
    [
        HeaderNames.Authorization, HeaderNames.ContentType, HeaderNames.IfMatch, HeaderNames.IfNoneMatch,
        "Prefer", RequestCorrelation.ClientRequestIdHeader, HeaderNames.ContentRange,
    ];

    public static readonly IReadOnlyList<string> ExposedHeaders =
    [
        HeaderNames.RetryAfter, RequestCorrelation.RequestIdHeader, RequestCorrelation.ClientRequestIdHeader,
        HeaderNames.Location, HeaderNames.ETag, "Preference-Applied",
    ];

    /// <summary>How long a browser may cache a preflight answer.</summary>
    public static readonly TimeSpan PreflightMaxAge = TimeSpan.FromHours(1);

    public static IServiceCollection AddGraphCors(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<CorsSettings>()
            .Bind(configuration.GetSection(CorsSettings.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CorsSettings>, CorsSettingsValidator>());

        services.AddCors();
        services.AddOptions<CorsOptions>()
            .Configure<IOptions<CorsSettings>>(static (cors, settings) =>
                cors.AddPolicy(PolicyName, policy => Build(policy, settings.Value)));
        return services;
    }

    /// <summary>
    /// Applies the policy to an endpoint or group <b>and</b> lets its endpoints match a CORS preflight.
    /// Minimal-API endpoints declare their verbs with <see cref="HttpMethodMetadata"/> that does not
    /// accept preflights, so without this an <c>OPTIONS</c> to a GET-only route never reaches the
    /// CORS middleware and the browser sees a 404.
    /// </summary>
    public static TBuilder RequireNightMailCors<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireCors(PolicyName);
        // Finally: the verb metadata is added after ordinary conventions run.
        builder.Finally(static endpoint =>
        {
            if (endpoint.Metadata.OfType<HttpMethodMetadata>().LastOrDefault() is { AcceptCorsPreflight: false } methods)
            {
                endpoint.Metadata.Add(new HttpMethodMetadata(methods.HttpMethods, acceptCorsPreflight: true));
            }
        });
        return builder;
    }

    internal static void Build(CorsPolicyBuilder policy, CorsSettings settings)
    {
        // An empty origin list is a policy that matches nothing: the CORS middleware then adds no
        // Access-Control-* headers and a browser blocks the response.
        policy
            .WithOrigins([.. settings.AllowedOrigins.Select(static o => o.TrimEnd('/'))])
            .WithMethods([.. AllowedMethods])
            .WithHeaders([.. AllowedHeaders])
            .WithExposedHeaders([.. ExposedHeaders])
            .SetPreflightMaxAge(PreflightMaxAge);
    }
}

internal sealed class CorsSettingsValidator : IValidateOptions<CorsSettings>
{
    public ValidateOptionsResult Validate(string? name, CorsSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = options.AllowedOrigins
            .Where(static o => !IsOrigin(o))
            .Select(static o => $"Cors:AllowedOrigins entry '{o}' must be an http(s) origin with no path, query or wildcard.")
            .ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsOrigin(string value) =>
        !value.Contains('*', StringComparison.Ordinal)
        && Uri.TryCreate(value.TrimEnd('/'), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
}
