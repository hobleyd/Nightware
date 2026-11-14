using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Http;
using Nightware.Application.Directory;

namespace Nightware.Adapters.Dav.Directory;

internal static class DavDirectoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IUserPhotoReader"/> (Phase 5A) on Nextcloud's avatar endpoint. Called from the
    /// Contacts hook, the only DAV registration hook Phase 5A owns.
    /// </summary>
    public static IServiceCollection AddDavDirectory(this IServiceCollection services)
    {
        // Same pipeline as DavClient (resilience outermost, then the per-request credential), but never
        // following redirects: Nextcloud redirects an unauthenticated request to its login page, which must
        // read as a failure, not as an HTML "photo".
        var client = services.AddHttpClient<IUserPhotoReader, NextcloudAvatarReader>(static http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false });
        client.AddResilienceHandler(DavResilience.PipelineName, static (builder, context) =>
            DavResilience.Configure(builder, context.ServiceProvider.GetRequiredService<IOptions<DavOptions>>().Value));
        client.AddHttpMessageHandler<DavAuthenticationHandler>();
        return services;
    }
}
