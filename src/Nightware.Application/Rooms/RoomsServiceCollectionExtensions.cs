using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Rooms;

public static class RoomsServiceCollectionExtensions
{
    /// <summary>Registers the Rooms use cases. Owned by the phase that builds Rooms.</summary>
    public static IServiceCollection AddRoomsUseCases(this IServiceCollection services) => services;
}
