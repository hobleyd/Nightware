using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Directory;

public static class DirectoryAdapterServiceCollectionExtensions
{
    /// <summary>Registers the Directory adapter: core client plus every feature port it implements.</summary>
    public static IServiceCollection AddDirectoryAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
