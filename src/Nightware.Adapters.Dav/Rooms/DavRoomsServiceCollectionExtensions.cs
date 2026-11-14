using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Dav.Rooms;

internal static class DavRoomsServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations. Owned by Phase 4.</summary>
    public static IServiceCollection AddDavRooms(this IServiceCollection services) => services;
}
