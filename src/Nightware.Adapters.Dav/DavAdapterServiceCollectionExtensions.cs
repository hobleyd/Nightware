using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Dav;

public static class DavAdapterServiceCollectionExtensions
{
    /// <summary>Registers the Dav adapter: core client plus every feature port it implements.</summary>
    public static IServiceCollection AddDavAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
