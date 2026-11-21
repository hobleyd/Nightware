using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Application.Administration.Subscriptions;

namespace Nightware.Adapters.Subscriptions.Administration;

public static class SubscriptionAdministrationServiceCollectionExtensions
{
    /// <summary>
    /// The admin console's view of change notifications: <see cref="IChangeSubscriptions"/> and the webhook client that
    /// tells an app its subscription was removed. Needs only <c>Subscriptions:ConnectionString</c>, never the sealing
    /// key; no poller runs here.
    /// </summary>
    public static IServiceCollection AddSubscriptionAdministration(this IServiceCollection services, IConfiguration configuration)
    {
        SubscriptionsAdapterServiceCollectionExtensions.AddStoreAndEndpoint(services, configuration);
        services.TryAddScoped<Nightware.Application.Subscriptions.INotificationEndpoint>(static sp =>
            SubscriptionsAdapterServiceCollectionExtensions.Settings(sp).HasDatabase
                ? sp.GetRequiredService<WebhookNotificationEndpoint>()
                : UnconfiguredSubscriptions.Instance);
        services.TryAddSingleton<PostgresChangeSubscriptions>();
        services.TryAddSingleton<IChangeSubscriptions>(static sp =>
            SubscriptionsAdapterServiceCollectionExtensions.Settings(sp).HasDatabase
                ? sp.GetRequiredService<PostgresChangeSubscriptions>()
                : UnconfiguredChangeSubscriptions.Instance);
        return services;
    }
}
