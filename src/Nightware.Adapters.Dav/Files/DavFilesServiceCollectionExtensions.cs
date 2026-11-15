using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Http;
using Nightware.Adapters.Dav.Directory;
using Nightware.Adapters.Dav.Office;
using Nightware.Application.Files;

namespace Nightware.Adapters.Dav.Files;

internal static class DavFilesServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISharedFileReader"/> (Phase 7) and, for its PDF previews, the Collabora
    /// <see cref="IDocumentConverter"/>.
    /// </summary>
    public static IServiceCollection AddDavFiles(this IServiceCollection services, IConfiguration configuration)
    {
        // Public shares: no credential handler at all — the share is the authorisation.
        var shares = services.AddHttpClient<NextcloudPublicShares>(static http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(static (handler, _) => NoCookiesNoRedirects(handler));
        shares.AddResilienceHandler(DavResilience.PipelineName, static (builder, context) =>
            DavResilience.Configure(builder, context.ServiceProvider.GetRequiredService<IOptions<DavOptions>>().Value));

        // File-id search: the DavClient pipeline (resilience outermost, then the caller's credential).
        var search = services.AddHttpClient<NextcloudFileIdSearch>(static http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(static (handler, _) => NoCookiesNoRedirects(handler));
        search.AddResilienceHandler(DavResilience.PipelineName, static (builder, context) =>
            DavResilience.Configure(builder, context.ServiceProvider.GetRequiredService<IOptions<DavOptions>>().Value));
        search.AddHttpMessageHandler<DavAuthenticationHandler>();

        services.TryAddScoped<ISharedFileReader, NextcloudSharedFileReader>();
        return services.AddDavOffice(configuration);
    }

    private static void NoCookiesNoRedirects(HttpMessageHandler handler)
    {
        DavHandlers.DisableRedirects(handler);
        DavHandlers.DisableCookies(handler);
    }
}
