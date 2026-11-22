using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Directory;

public static class DirectoryServiceCollectionExtensions
{
    /// <summary>Registers the Directory use cases (Phase 5A; groups, reporting lines and the organization, phase 20). Scoped: they consume per-request ports.</summary>
    public static IServiceCollection AddDirectoryUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<GetSignedInUser>();
        services.TryAddScoped<GetUser>();
        services.TryAddScoped<ListUsers>();
        services.TryAddScoped<GetUserPhoto>();
        services.TryAddScoped<GetSignedInUserPhoto>();
        services.TryAddScoped<GetUserPhotoInfo>();
        services.TryAddScoped<SetUserPhoto>();
        services.TryAddScoped<DeleteUserPhoto>();

        // Phase 20: groups, reporting lines and the organization.
        services.TryAddScoped<GetManager>();
        services.TryAddScoped<ListDirectReports>();
        services.TryAddScoped<ListMemberOf>();
        services.TryAddScoped<ListGroups>();
        services.TryAddScoped<GetGroup>();
        services.TryAddScoped<ListGroupMembers>();
        services.TryAddScoped<ListOrganizations>();
        services.TryAddScoped<GetOrganization>();
        return services;
    }
}
