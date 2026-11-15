using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Directory;
using Nightware.Application.Files;

namespace Nightware.Adapters.Dav.Office;

internal static class DavOfficeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IDocumentConverter"/>: Collabora when <c>Collabora:BaseUrl</c> is set,
    /// otherwise <see cref="UnconfiguredDocumentConverter"/> (no document is offered as PDF).
    /// </summary>
    public static IServiceCollection AddDavOffice(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CollaboraOptions>().Bind(configuration.GetSection(CollaboraOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CollaboraOptions>, CollaboraOptionsValidator>());

        // The conversion's own timeout governs (CollaboraOptions.Timeout); no retries for a document upload.
        services.AddHttpClient<CollaboraDocumentConverter>(static http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(static (handler, _) =>
            {
                DavHandlers.DisableRedirects(handler);
                DavHandlers.DisableCookies(handler);
            });
        services.TryAddTransient<IDocumentConverter>(static sp =>
            sp.GetRequiredService<IOptions<CollaboraOptions>>().Value.BaseUrl is null
                ? new UnconfiguredDocumentConverter()
                : sp.GetRequiredService<CollaboraDocumentConverter>());
        return services;
    }
}
