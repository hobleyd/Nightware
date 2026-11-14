using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Jmap.Mail.Read;

internal static class JmapMailReadServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations. Owned by Phase 2.</summary>
    public static IServiceCollection AddJmapMailRead(this IServiceCollection services) => services;
}
