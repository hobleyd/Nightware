using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Directory;

public static class DirectoryServiceCollectionExtensions
{
    /// <summary>Registers the Directory use cases. Owned by the phase that builds Directory.</summary>
    public static IServiceCollection AddDirectoryUseCases(this IServiceCollection services) => services;
}
