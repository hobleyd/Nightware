using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Application.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

internal static class JmapMailWriteServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations. Owned by Phase 3.</summary>
    public static IServiceCollection AddJmapMailWrite(this IServiceCollection services)
    {
        services.AddScoped<IMailMessageWriter, JmapMailMessageWriter>();
        services.AddScoped<IMailFolderWriter, JmapMailFolderWriter>();
        services.AddScoped<IDraftStore, JmapDraftStore>();
        services.AddScoped<IMailSender, JmapMailSender>();
        services.AddScoped<IMailImporter, JmapMailImporter>();

        // The locator is Mail/Read's (registered before this); the stand-in only keeps the container
        // valid until it is, and answers 501.
        services.TryAddScoped<IJmapMessageLocator, UnavailableMessageLocator>();
        return services;
    }
}
