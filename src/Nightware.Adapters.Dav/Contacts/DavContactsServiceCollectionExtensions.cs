using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Dav.Contacts;

internal static class DavContactsServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations. Owned by Phase 5A.</summary>
    public static IServiceCollection AddDavContacts(this IServiceCollection services) => services;
}
