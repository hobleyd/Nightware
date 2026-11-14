using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Application.Contacts;

namespace Nightware.Adapters.Dav.Contacts;

internal static class DavContactsServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations (Phase 5A): <see cref="IContactStore"/>.</summary>
    public static IServiceCollection AddDavContacts(this IServiceCollection services)
    {
        services.TryAddScoped<IContactStore, DavContactStore>();
        return services;
    }
}
