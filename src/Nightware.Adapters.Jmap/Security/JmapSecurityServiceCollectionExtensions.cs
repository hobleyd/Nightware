using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Jmap.Security;

internal static class JmapSecurityServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations. Owned by Phase 5B.</summary>
    public static IServiceCollection AddJmapSecurity(this IServiceCollection services) => services;
}
