using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Dav.Calls;

internal static class DavCallsServiceCollectionExtensions
{
    /// <summary>Registers the Calls ports (phase 26, ADR 0009): delegated calls that ring through Talk.</summary>
    public static IServiceCollection AddDavCalls(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
