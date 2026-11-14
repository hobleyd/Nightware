using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Files;

public static class FilesServiceCollectionExtensions
{
    /// <summary>Registers the Files use cases. Owned by the phase that builds Files.</summary>
    public static IServiceCollection AddFilesUseCases(this IServiceCollection services) => services;
}
