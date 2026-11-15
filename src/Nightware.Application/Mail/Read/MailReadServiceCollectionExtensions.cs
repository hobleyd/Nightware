using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Mail.Read;

public static class MailReadServiceCollectionExtensions
{
    /// <summary>Registers the mail read use cases. Owned by Phase 2.</summary>
    public static IServiceCollection AddMailReadUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<ListMailFolders>();
        services.TryAddScoped<GetMailFolder>();
        services.TryAddScoped<ListMessages>();
        services.TryAddScoped<GetMessage>();
        services.TryAddScoped<GetMessageMime>();
        services.TryAddScoped<ListMessageAttachments>();
        services.TryAddScoped<GetMessageAttachment>();
        services.TryAddScoped<GetMessageAttachmentContent>();
        services.TryAddScoped<SyncMessages>();
        return services;
    }
}
