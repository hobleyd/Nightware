using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Files;

public static class FilesServiceCollectionExtensions
{
    /// <summary>Registers the Files use cases (Phase 7: documents linked from mail).</summary>
    public static IServiceCollection AddFilesUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<GetSharedItem>();
        services.TryAddScoped<PrepareSharedItemDownload>();
        services.TryAddScoped<DownloadSharedItem>();
        return services;
    }
}
