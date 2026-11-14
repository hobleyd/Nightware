using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.People;

public static class PeopleServiceCollectionExtensions
{
    /// <summary>Registers the People use cases (Phase 5A).</summary>
    public static IServiceCollection AddPeopleUseCases(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PeopleRanker>();
        services.TryAddScoped<ListPeople>();
        return services;
    }
}
