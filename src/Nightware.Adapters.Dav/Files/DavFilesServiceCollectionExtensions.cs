using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Dav.Files;

internal static class DavFilesServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations. Owned by Phase 5B.</summary>
    public static IServiceCollection AddDavFiles(this IServiceCollection services) => services;
}
