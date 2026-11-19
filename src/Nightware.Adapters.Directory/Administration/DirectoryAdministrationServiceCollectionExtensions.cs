using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Application.Administration;

namespace Nightware.Adapters.Directory.Administration;

public static class DirectoryAdministrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Keycloak side of the admin console (ADR 0002): <see cref="IEntityRegistry"/>,
    /// <see cref="IIdentityAccounts"/>, <see cref="IAdminScopeSource"/> and Keycloak's
    /// <see cref="IBackendStatus"/>. Requires <see cref="DirectoryAdapterServiceCollectionExtensions.AddKeycloakCore"/>
    /// (called once, with <c>Directory:</c> bound to the console's own client). Never called by the Graph host.
    /// </summary>
    public static IServiceCollection AddKeycloakAdministration(this IServiceCollection services)
    {
        // Scoped: they wrap the transient typed admin client and are consumed by scoped use cases.
        services.TryAddScoped<KeycloakEntityAdmins>();
        services.TryAddScoped<IEntityRegistry, KeycloakEntityRegistry>();
        services.TryAddScoped<IIdentityAccounts, KeycloakIdentityAccounts>();
        services.TryAddScoped<IAdminScopeSource, KeycloakAdminScopeSource>();
        services.TryAddScoped<IEntityGroups, KeycloakEntityGroups>();

        // Enumerable: Stalwart and Nextcloud register their own IBackendStatus.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IBackendStatus, KeycloakBackendStatus>());
        return services;
    }
}
