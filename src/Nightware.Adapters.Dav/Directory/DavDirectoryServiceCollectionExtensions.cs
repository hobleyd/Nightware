using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Dav.Directory;

internal static class DavDirectoryServiceCollectionExtensions
{
    /// <summary>Registers Nextcloud-backed directory ports (user photos). Owned by Phase 5A.</summary>
    public static IServiceCollection AddDavDirectory(this IServiceCollection services) => services;
}
