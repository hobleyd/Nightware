using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightware.Application.Rooms;

namespace Nightware.Adapters.Dav.Rooms;

internal static class DavRoomsServiceCollectionExtensions
{
    /// <summary>Registers the room directory.</summary>
    public static IServiceCollection AddDavRooms(this IServiceCollection services)
    {
        services.TryAddScoped<IRoomDirectory, DavRoomDirectory>();
        return services;
    }
}
