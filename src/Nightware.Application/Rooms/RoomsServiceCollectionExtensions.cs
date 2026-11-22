using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Rooms;

public static class RoomsServiceCollectionExtensions
{
    /// <summary>Registers the Rooms use cases.</summary>
    public static IServiceCollection AddRoomsUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<ListRooms>();
        services.TryAddScoped<FindRooms>();
        services.TryAddScoped<ListRoomLists>();
        services.TryAddScoped<ListRoomsInList>();
        return services;
    }
}
