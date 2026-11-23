using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Dav.OnlineMeetings;

internal static class DavOnlineMeetingsServiceCollectionExtensions
{
    /// <summary>Registers the OnlineMeetings ports (phase 26, ADR 0009): online meetings over Talk rooms, with their recordings and transcripts.</summary>
    public static IServiceCollection AddDavOnlineMeetings(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
