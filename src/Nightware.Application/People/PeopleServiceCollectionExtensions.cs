using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.People;

public static class PeopleServiceCollectionExtensions
{
    /// <summary>Registers the People use cases. Owned by the phase that builds People.</summary>
    public static IServiceCollection AddPeopleUseCases(this IServiceCollection services) => services;
}
