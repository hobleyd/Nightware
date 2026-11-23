using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Dav.Presence;

internal static class DavPresenceServiceCollectionExtensions
{
    /// <summary>Registers the Presence ports (phase 25, ADR 0009): presence over Nextcloud user status.</summary>
    public static IServiceCollection AddDavPresence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
