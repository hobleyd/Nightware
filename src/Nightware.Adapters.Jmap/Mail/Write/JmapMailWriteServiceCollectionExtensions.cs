using Microsoft.Extensions.DependencyInjection;
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

        // Message ids resolve through Mail/Read's IJmapMessageLocator (ADR 0001 §41), registered before this.
        return services;
    }
}
