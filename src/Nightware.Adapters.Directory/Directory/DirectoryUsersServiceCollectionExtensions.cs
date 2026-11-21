using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Application.Directory;

namespace Nightware.Adapters.Directory.Directory;

internal static class DirectoryUsersServiceCollectionExtensions
{
    /// <summary>
    /// Registers this feature's port implementations (Phase 5A): <see cref="IUserDirectory"/>, and the addresses of
    /// groups and shared mailboxes (<see cref="IDirectoryAddresses"/>). User photos live in Nextcloud and are read by
    /// <c>Adapters.Dav</c>.
    /// </summary>
    public static IServiceCollection AddDirectoryUsers(this IServiceCollection services)
    {
        // Scoped: it wraps the transient typed admin client and is consumed by the scoped MailboxResolver.
        services.TryAddScoped<IUserDirectory, KeycloakUserDirectory>();
        services.TryAddScoped<IDirectoryAddresses, KeycloakDirectoryAddresses>();
        services.TryAddScoped<Nightware.Application.MailboxSettings.IInternalDomains, Nightware.Adapters.Directory.MailboxSettings.KeycloakInternalDomains>();
        return services;
    }
}
