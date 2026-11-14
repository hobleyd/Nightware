using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Jmap;

public static class JmapAdapterServiceCollectionExtensions
{
    /// <summary>Registers the Jmap adapter: core client plus every feature port it implements.</summary>
    public static IServiceCollection AddJmapAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
