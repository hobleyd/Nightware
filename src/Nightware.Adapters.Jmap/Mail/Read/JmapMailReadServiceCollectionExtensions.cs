using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Application.Mail;

namespace Nightware.Adapters.Jmap.Mail.Read;

internal static class JmapMailReadServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations, and the message-id locator Phase 3 shares. Owned by Phase 2.</summary>
    public static IServiceCollection AddJmapMailRead(this IServiceCollection services)
    {
        services.TryAddScoped<IJmapMessageLocator, JmapMessageLocator>();
        services.TryAddScoped<IMailFolderReader, JmapMailFolderReader>();
        services.TryAddScoped<IMailMessageReader, JmapMailMessageReader>();
        services.TryAddScoped<IMailDeltaReader, JmapMailDeltaReader>();
        services.TryAddScoped<IMailFolderDeltaReader, JmapMailFolderDeltaReader>();
        services.TryAddScoped<IRecentCorrespondents, JmapRecentCorrespondents>();
        return services;
    }
}
