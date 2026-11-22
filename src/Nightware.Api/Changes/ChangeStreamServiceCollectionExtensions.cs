using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Api.Discovery;

namespace Nightware.Api.Changes;

public static class ChangeStreamServiceCollectionExtensions
{
    /// <summary>The change stream's options, its per-user cap, and its URL in the discovery document (ADR 0007).</summary>
    public static IServiceCollection AddChangeStream(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<ChangeStreamOptions>()
            .Bind(configuration.GetSection(ChangeStreamOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ChangeStreamOptions>, ChangeStreamOptionsValidator>());
        services.TryAddSingleton<ChangeStreamLimiter>();
        services.AddOptions<DiscoveryOptions>()
            .PostConfigure<IOptions<ChangeStreamOptions>>(static (discovery, stream) =>
                discovery.ChangesUrl = stream.Value.Enabled ? discovery.ChangesUrl ?? DerivedChangesUrl(discovery.GraphBaseUrl) : null);
        return services;
    }

    /// <summary>
    /// <c>https://graph.example.com/v1.0</c> → <c>https://graph.example.com/nightware/v1/changes</c>: the stream is
    /// mapped beside <c>/v1.0</c>, so behind a proxy that maps a prefix to this service it sits under the same one.
    /// Null when the Graph root does not end in <c>/v1.0</c>; then only an explicit <c>Discovery:ChangesUrl</c> advertises it.
    /// </summary>
    public static string? DerivedChangesUrl(string graphBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(graphBaseUrl);
        const string version = "/v1.0";
        var trimmed = graphBaseUrl.TrimEnd('/');
        return trimmed.EndsWith(version, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^version.Length] + ChangeStreamEndpoints.Path
            : null;
    }
}
