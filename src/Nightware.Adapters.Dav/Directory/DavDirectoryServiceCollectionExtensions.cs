using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Http;
using Nightware.Application.Directory;

namespace Nightware.Adapters.Dav.Directory;

internal static class DavDirectoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IUserPhotoReader"/> (Phase 5A) and <see cref="IUserPhotoWriter"/> (phase 19) on Nextcloud's avatar endpoint.
    /// </summary>
    public static IServiceCollection AddDavDirectory(this IServiceCollection services)
    {
        // Same pipeline as DavClient (resilience outermost, then the per-request credential). The pooled
        // primary handler is adjusted, not replaced, so host-wide settings (proxy, TLS trust) still apply:
        // no redirects (Nextcloud redirects an unauthenticated request to its login page, which must read
        // as a failure, not as an HTML "photo") and no cookie jar (see DavHandlers.DisableCookies;
        // the caller's own session goes back through NextcloudSessionHandler).
        Configure(services.AddHttpClient<IUserPhotoReader, NextcloudAvatarReader>(static http => http.Timeout = Timeout.InfiniteTimeSpan));
        Configure(services.AddHttpClient<IUserPhotoWriter, NextcloudAvatarWriter>(static http => http.Timeout = Timeout.InfiniteTimeSpan));
        return services;
    }

    private static void Configure(IHttpClientBuilder client)
    {
        client.ConfigurePrimaryHttpMessageHandler(static (handler, _) =>
        {
            DavHandlers.DisableRedirects(handler);
            DavHandlers.DisableCookies(handler);
        });
        client.AddResilienceHandler(DavResilience.PipelineName, static (builder, context) =>
            DavResilience.Configure(builder, context.ServiceProvider.GetRequiredService<IOptions<DavOptions>>().Value));
        client.AddHttpMessageHandler<DavAuthenticationHandler>();
        client.WithNextcloudSession();
        client.WithNightwareUserAgent();
    }
}
