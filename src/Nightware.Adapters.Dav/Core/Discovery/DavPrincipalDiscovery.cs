using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Core.Discovery;

/// <summary>
/// RFC 5397/4791/6352/6638 principal discovery. Always starts from
/// <c>current-user-principal</c> — with OIDC the Nextcloud uid is frequently not the
/// email, so principal URLs are never constructed by hand.
/// </summary>
public sealed class DavPrincipalDiscovery : IDavPrincipalDiscovery
{
    internal static readonly XName[] PrincipalProperties =
    [
        DavNames.DisplayName,
        DavNames.CalendarHomeSet,
        DavNames.AddressBookHomeSet,
        DavNames.ScheduleOutboxUrl,
        DavNames.ScheduleInboxUrl,
        DavNames.CalendarUserAddressSet,
        DavNames.CalendarUserType,
        DavNames.EmailAddress,
        DavNames.ResourceType,
    ];

    private const string CachePrefix = "nightware:dav:principal:";
    private readonly IDavClient _client;
    private readonly IBackendCredentials _credentials;
    private readonly IMemoryCache _cache;
    private readonly DavPaths _paths;
    private readonly DavOptions _options;

    public DavPrincipalDiscovery(IDavClient client, IBackendCredentials credentials, IMemoryCache cache, DavPaths paths, IOptions<DavOptions> options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    }

    public async Task<Result<DavPrincipal>> DiscoverAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mailbox.Address))
        {
            return Error.Validation("A mailbox address is required.", "mailbox");
        }

        var identity = await IdentityAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.IsSuccess)
        {
            return identity.Error;
        }

        var key = $"{CachePrefix}{identity.Value}:{mailbox.Address.ToUpperInvariant()}";
        if (_cache.TryGetValue(key, out DavPrincipal? cached) && cached is not null)
        {
            return cached;
        }

        var self = await CurrentUserCoreAsync(identity.Value, cancellationToken).ConfigureAwait(false);
        if (!self.IsSuccess)
        {
            return self;
        }

        var result = self.Value.HasAddress(mailbox.Address)
            ? self
            : await SearchAsync(mailbox.Address, self.Value, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _cache.Set(key, result.Value, _options.DiscoveryCacheDuration);
        }

        return result;
    }

    public async Task<Result<DavPrincipal>> CurrentUserAsync(CancellationToken cancellationToken)
    {
        var identity = await IdentityAsync(cancellationToken).ConfigureAwait(false);
        return identity.IsSuccess
            ? await CurrentUserCoreAsync(identity.Value, cancellationToken).ConfigureAwait(false)
            : identity.Error;
    }

    private async Task<Result<DavPrincipal>> CurrentUserCoreAsync(string identity, CancellationToken cancellationToken)
    {
        var key = $"{CachePrefix}{identity}:@self";
        if (_cache.TryGetValue(key, out DavPrincipal? cached) && cached is not null)
        {
            return cached;
        }

        var root = await _client.PropFindAsync(_paths.RootPath, DavDepth.Zero, [DavNames.CurrentUserPrincipal], cancellationToken).ConfigureAwait(false);
        if (!root.IsSuccess)
        {
            return root.Error;
        }

        var principalHref = root.Value.Responses.Select(r => r.HrefOf(DavNames.CurrentUserPrincipal)).FirstOrDefault(h => h is not null);
        if (principalHref is null)
        {
            // Sabre answers <d:unauthenticated/> when the credential was not accepted as a user.
            return Error.BackendAuthFailed("The groupware server did not identify the caller.");
        }

        var principal = await _client.PropFindAsync(principalHref, DavDepth.Zero, PrincipalProperties, cancellationToken).ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal.Error;
        }

        var response = principal.Value.For(principalHref) ?? FirstOrNull(principal.Value);
        if (response is null)
        {
            return Error.NotFound("The caller's calendar principal was not found.");
        }

        var self = ToPrincipal(response, isCurrentUser: true);
        _cache.Set(key, self, _options.DiscoveryCacheDuration);
        return self;
    }

    private async Task<Result<DavPrincipal>> SearchAsync(string address, DavPrincipal self, CancellationToken cancellationToken)
    {
        var report = DavRequestBodies.PrincipalSearchByEmail(address, PrincipalProperties);
        var search = await _client.ReportAsync(_paths.RootPath + "principals/", DavDepth.Zero, report, cancellationToken).ConfigureAwait(false);
        if (!search.IsSuccess)
        {
            return search.Error;
        }

        var match = search.Value.Responses
            .Where(r => r.IsFound)
            .Select(r => ToPrincipal(r, isCurrentUser: false))
            .FirstOrDefault(p => p.HasAddress(address));
        if (match is null)
        {
            return Error.NotFound($"No calendar principal was found for {address}.");
        }

        if (string.Equals(match.Href.TrimEnd('/'), self.Href.TrimEnd('/'), StringComparison.Ordinal))
        {
            return self;
        }

        if (match.CalendarHomeSet is not null || match.IsRoomOrResource)
        {
            return match;
        }

        // Some deployments omit home sets from search results; ask the principal directly.
        var detail = await _client.PropFindAsync(match.Href, DavDepth.Zero, PrincipalProperties, cancellationToken).ConfigureAwait(false);
        if (!detail.IsSuccess)
        {
            return detail.Error.Kind == ErrorKind.NotFound ? match : detail.Error;
        }

        var response = detail.Value.For(match.Href) ?? FirstOrNull(detail.Value);
        return response is null ? match : ToPrincipal(response, isCurrentUser: false);
    }

    private async Task<Result<string>> IdentityAsync(CancellationToken cancellationToken)
    {
        var credential = await _credentials.ForAsync(Backend.Groupware, cancellationToken).ConfigureAwait(false);
        if (!credential.IsSuccess)
        {
            return credential.Error;
        }

        if (credential.Value.ActAs is not null)
        {
            return Error.Unsupported("Nextcloud has no DAV impersonation; a service account cannot act as another user.");
        }

        return IdentityOf(credential.Value);
    }

    private static DavResponse? FirstOrNull(DavMultistatus multistatus) =>
        multistatus.Responses.Count > 0 ? multistatus.Responses[0] : null;

    /// <summary>A stable, non-reversible cache key for a credential (never the raw token).</summary>
    internal static string IdentityOf(BackendCredential credential)
    {
        var material = $"{credential.Scheme}\n{credential.Parameter}\n{credential.ActAs}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }

    internal static DavPrincipal ToPrincipal(DavResponse response, bool isCurrentUser)
    {
        var addresses = response.HrefsOf(DavNames.CalendarUserAddressSet);
        var emails = addresses
            .Where(a => a.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            .Select(a => Uri.UnescapeDataString(a["mailto:".Length..]))
            .ToList();
        var sabreEmail = response.Text(DavNames.EmailAddress)?.Trim();
        if (!string.IsNullOrEmpty(sabreEmail) && !emails.Contains(sabreEmail, StringComparer.OrdinalIgnoreCase))
        {
            emails.Add(sabreEmail);
        }

        return new DavPrincipal(response.Href, isCurrentUser)
        {
            DisplayName = response.Text(DavNames.DisplayName),
            CalendarHomeSet = response.HrefOf(DavNames.CalendarHomeSet),
            AddressBookHomeSet = response.HrefOf(DavNames.AddressBookHomeSet),
            ScheduleOutboxUrl = response.HrefOf(DavNames.ScheduleOutboxUrl),
            ScheduleInboxUrl = response.HrefOf(DavNames.ScheduleInboxUrl),
            CalendarUserAddresses = addresses,
            CalendarUserType = response.Text(DavNames.CalendarUserType)?.Trim(),
            EmailAddresses = emails,
        };
    }
}
