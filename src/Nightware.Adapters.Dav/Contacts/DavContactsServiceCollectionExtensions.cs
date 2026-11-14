using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Adapters.Dav.Directory;
using Nightware.Application.Contacts;

namespace Nightware.Adapters.Dav.Contacts;

internal static class DavContactsServiceCollectionExtensions
{
    /// <summary>
    /// Registers this feature's port implementations (Phase 5A): <see cref="IContactStore"/> and, because
    /// <c>AddDavAdapter</c> has no Directory hook, the Directory feature's Nextcloud photo reader.
    /// </summary>
    public static IServiceCollection AddDavContacts(this IServiceCollection services)
    {
        services.TryAddScoped<IContactStore, DavContactStore>();
        return services.AddDavDirectory();
    }
}
