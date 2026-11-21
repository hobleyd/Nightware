using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Application.Subscriptions;

namespace Nightware.Adapters.Directory.Subscriptions;

internal static class DirectorySubscriptionsServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ISubscriberCredentials"/> (phase 16): per request, as it reads the caller's token.</summary>
    public static IServiceCollection AddDirectorySubscriptions(this IServiceCollection services)
    {
        services.TryAddScoped<ISubscriberCredentials, KeycloakSubscriberCredentials>();
        services.TryAddSingleton<ISubscriberSessions, KeycloakSubscriberSessions>();
        return services;
    }
}
