using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Application.Contacts;

namespace Nightware.Adapters.Dav.Contacts;

internal static class DavContactsServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations: <see cref="IContactStore"/>, <see cref="IContactFolderStore"/> and <see cref="IContactDelta"/>.</summary>
    public static IServiceCollection AddDavContacts(this IServiceCollection services)
    {
        services.TryAddScoped<DavContactStore>();
        services.TryAddScoped<IContactStore>(static sp => sp.GetRequiredService<DavContactStore>());
        services.TryAddScoped<IContactFolderStore, DavContactFolderStore>();
        services.TryAddScoped<IContactDelta, DavContactDelta>();
        return services;
    }
}
