using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Core.Discovery;

/// <summary>Finds the DAV principal (and its home sets / scheduling URLs) behind a mailbox.</summary>
public interface IDavPrincipalDiscovery
{
    /// <summary>
    /// The caller's own principal when <paramref name="mailbox"/> is one of its addresses;
    /// otherwise the principal found by <c>principal-property-search</c> on
    /// <c>calendar-user-address-set</c> (users, rooms and resources). <see cref="ErrorKind.NotFound"/>
    /// when nothing matches or Nextcloud's sharing settings hide the principal.
    /// Results are cached per credential identity.
    /// </summary>
    Task<Result<DavPrincipal>> DiscoverAsync(MailboxRef mailbox, CancellationToken cancellationToken);

    /// <summary>The caller's own principal regardless of mailbox.</summary>
    Task<Result<DavPrincipal>> CurrentUserAsync(CancellationToken cancellationToken);
}
