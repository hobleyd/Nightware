using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Adapters.Dav.Core;
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
        DisableDavClientCookies(services);
        return services.AddDavDirectory();
    }

    /// <summary>
    /// Stops the shared <see cref="IDavClient"/> handler from keeping cookies.
    /// </summary>
    /// <remarks>
    /// Nextcloud answers a bearer request with a session cookie. The default primary handler stores it,
    /// and <c>IHttpClientFactory</c> pools that handler across requests and users. The next DAV request
    /// then carries the cookie next to its bearer token, and Nextcloud rejects it with 401 (the CSRF check
    /// for a cookie-authenticated session) — observed live: every DAV call after the first in a request
    /// failed. A pooled cookie jar would also mix one user's session into another user's requests.
    /// This belongs in <c>AddDavCore</c> (frozen for feature phases); it is applied here so contacts
    /// work, and is idempotent should the core adopt it.
    /// </remarks>
    internal static void DisableDavClientCookies(IServiceCollection services) =>
        services.AddHttpClient(nameof(IDavClient)).ConfigurePrimaryHttpMessageHandler(static (handler, _) => DavHandlers.DisableCookies(handler));
}
