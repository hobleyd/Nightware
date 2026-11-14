using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Jmap.MailboxSettings;

internal static class JmapMailboxSettingsServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations. Owned by Phase 5B.</summary>
    public static IServiceCollection AddJmapMailboxSettings(this IServiceCollection services) => services;
}
