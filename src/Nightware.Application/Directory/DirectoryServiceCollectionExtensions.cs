using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Directory;

public static class DirectoryServiceCollectionExtensions
{
    /// <summary>Registers the Directory use cases (Phase 5A). Scoped: they consume per-request ports.</summary>
    public static IServiceCollection AddDirectoryUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<GetSignedInUser>();
        services.TryAddScoped<GetUser>();
        services.TryAddScoped<ListUsers>();
        services.TryAddScoped<GetUserPhoto>();
        return services;
    }
}
