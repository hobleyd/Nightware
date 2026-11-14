using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Dav.Tasks;

internal static class DavTasksServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations. Owned by Phase 5B.</summary>
    public static IServiceCollection AddDavTasks(this IServiceCollection services) => services;
}
