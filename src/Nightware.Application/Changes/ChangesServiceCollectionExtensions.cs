using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Changes;

public static class ChangesServiceCollectionExtensions
{
    /// <summary>Registers the change stream's watcher (ADR 0007). The adapters register the <see cref="IChangeStateProbe"/>s.</summary>
    public static IServiceCollection AddChangesUseCases(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<WatchChanges>();
        return services;
    }
}
