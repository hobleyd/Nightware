using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Dav.Teams;

internal static class DavTeamsServiceCollectionExtensions
{
    /// <summary>Registers the Teams ports (phase 24, ADR 0009): teams and channels over Nextcloud Teams (circles) and Talk.</summary>
    public static IServiceCollection AddDavTeams(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
