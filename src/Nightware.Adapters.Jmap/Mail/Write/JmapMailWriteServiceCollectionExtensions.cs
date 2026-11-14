using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Jmap.Mail.Write;

internal static class JmapMailWriteServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations. Owned by Phase 3.</summary>
    public static IServiceCollection AddJmapMailWrite(this IServiceCollection services) => services;
}
