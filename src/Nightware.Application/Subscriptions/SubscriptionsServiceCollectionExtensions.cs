using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Subscriptions;

public static class SubscriptionsServiceCollectionExtensions
{
    /// <summary>Registers the change-notification use cases (phase 16).</summary>
    public static IServiceCollection AddSubscriptionsUseCases(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<SubscriptionsUseCase>();
        return services;
    }
}
