using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Directory.Directory;

internal static class DirectoryUsersServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations. Owned by Phase 5A.</summary>
    public static IServiceCollection AddDirectoryUsers(this IServiceCollection services) => services;
}
