using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Application.Directory;

namespace Nightware.Adapters.Directory.MailboxSettings;

internal static class DirectoryPreferencesServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations. Owned by Phase 5B.</summary>
    public static IServiceCollection AddDirectoryPreferences(this IServiceCollection services)
    {
        services.TryAddScoped<IUserPreferencesStore, KeycloakUserPreferencesStore>();
        return services;
    }
}
