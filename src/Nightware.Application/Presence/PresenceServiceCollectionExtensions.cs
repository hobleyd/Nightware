using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Presence;

public static class PresenceServiceCollectionExtensions
{
    /// <summary>Registers the Presence use cases (phase 25, ADR 0009): presence over Nextcloud user status.</summary>
    public static IServiceCollection AddPresenceUseCases(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<PresenceReader>();
        services.TryAddScoped<UserStatusWriter>();
        services.TryAddScoped<GetPresence>();
        services.TryAddScoped<GetPresencesByUserId>();
        services.TryAddScoped<SetPresence>();
        services.TryAddScoped<SetUserPreferredPresence>();
        services.TryAddScoped<ClearPresence>();
        services.TryAddScoped<ClearUserPreferredPresence>();
        services.TryAddScoped<SetStatusMessage>();
        return services;
    }
}
