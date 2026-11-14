using System.Globalization;
using System.Xml.Linq;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Core.Collections;

/// <summary>PROPFIND depth 1 on the home sets, mapped to collection records.</summary>
public sealed class DavCollectionCatalog : IDavCollectionCatalog
{
    internal static readonly XName[] CalendarProperties =
    [
        DavNames.ResourceType,
        DavNames.DisplayName,
        DavNames.CalendarDescription,
        DavNames.SupportedCalendarComponentSet,
        DavNames.CalendarColor,
        DavNames.CalendarOrder,
        DavNames.GetCTag,
        DavNames.SyncToken,
        DavNames.OwnerPrincipal,
        DavNames.OwnerDisplayName,
        DavNames.OwnCloudInvite,
        DavNames.OwnCloudReadOnly,
        DavNames.CurrentUserPrivilegeSet,
        DavNames.Deleted,
    ];

    internal static readonly XName[] AddressBookProperties =
    [
        DavNames.ResourceType,
        DavNames.DisplayName,
        DavNames.AddressBookDescription,
        DavNames.GetCTag,
        DavNames.SyncToken,
        DavNames.OwnerPrincipal,
        DavNames.OwnCloudReadOnly,
        DavNames.CurrentUserPrivilegeSet,
    ];

    private readonly IDavClient _client;

    public DavCollectionCatalog(IDavClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<Result<IReadOnlyList<DavCalendar>>> ListCalendarsAsync(DavPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.CalendarHomeSet is null)
        {
            return Error.NotFound("The principal has no calendar home.");
        }

        var listing = await _client.PropFindAsync(principal.CalendarHomeSet, DavDepth.One, CalendarProperties, cancellationToken).ConfigureAwait(false);
        if (!listing.IsSuccess)
        {
            return listing.Error;
        }

        IReadOnlyList<DavCalendar> calendars = listing.Value.Responses
            .Where(r => r.IsFound && r.HasResourceType(DavNames.Calendar) && r.Find(DavNames.Deleted) is null
                        && !r.HasResourceType(DavNs.CalendarServer + "subscribed"))
            .Select(r => ToCalendar(r, principal))
            .ToList();
        return Result<IReadOnlyList<DavCalendar>>.Success(calendars);
    }

    public async Task<Result<IReadOnlyList<DavAddressBook>>> ListAddressBooksAsync(DavPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.AddressBookHomeSet is null)
        {
            return Error.NotFound("The principal has no address book home.");
        }

        var listing = await _client.PropFindAsync(principal.AddressBookHomeSet, DavDepth.One, AddressBookProperties, cancellationToken).ConfigureAwait(false);
        if (!listing.IsSuccess)
        {
            return listing.Error;
        }

        IReadOnlyList<DavAddressBook> books = listing.Value.Responses
            .Where(r => r.IsFound && r.HasResourceType(DavNames.AddressBook))
            .Select(r =>
            {
                var owner = r.Text(DavNames.OwnerPrincipal)?.Trim();
                return new DavAddressBook(r.Href, r.Text(DavNames.DisplayName) ?? LastSegment(r.Href))
                {
                    Description = r.Text(DavNames.AddressBookDescription),
                    CTag = r.Text(DavNames.GetCTag),
                    SyncToken = r.Text(DavNames.SyncToken),
                    OwnerPrincipal = owner,
                    IsShared = IsSharedFrom(owner, principal),
                    IsReadOnly = IsReadOnly(r),
                };
            })
            .ToList();
        return Result<IReadOnlyList<DavAddressBook>>.Success(books);
    }

    internal static DavCalendar ToCalendar(DavResponse response, DavPrincipal principal)
    {
        var components = response.Find(DavNames.SupportedCalendarComponentSet)?
            .Elements(DavNames.Comp)
            .Select(c => (string?)c.Attribute("name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var owner = response.Text(DavNames.OwnerPrincipal)?.Trim();
        return new DavCalendar(response.Href, response.Text(DavNames.DisplayName) ?? LastSegment(response.Href))
        {
            Components = components,
            Description = response.Text(DavNames.CalendarDescription),
            Color = response.Text(DavNames.CalendarColor)?.Trim(),
            Order = int.TryParse(response.Text(DavNames.CalendarOrder), NumberStyles.Integer, CultureInfo.InvariantCulture, out var order) ? order : null,
            CTag = response.Text(DavNames.GetCTag),
            SyncToken = response.Text(DavNames.SyncToken),
            OwnerPrincipal = owner,
            OwnerDisplayName = response.Text(DavNames.OwnerDisplayName),
            IsShared = IsSharedFrom(owner, principal) || response.HasResourceType(DavNs.CalendarServer + "shared"),
            IsReadOnly = IsReadOnly(response),
        };
    }

    private static bool IsSharedFrom(string? owner, DavPrincipal principal)
    {
        if (string.IsNullOrEmpty(owner))
        {
            return false;
        }

        // Nextcloud reports owner-principal relative ("principals/users/bob"); principal hrefs are absolute.
        var ownerPath = "/" + owner.Trim('/') + "/";
        return !principal.Href.EndsWith(ownerPath, StringComparison.Ordinal)
               && !string.Equals(principal.Href.Trim('/'), owner.Trim('/'), StringComparison.Ordinal);
    }

    private static bool IsReadOnly(DavResponse response)
    {
        var readOnly = response.Text(DavNames.OwnCloudReadOnly)?.Trim();
        if (readOnly is "1" || string.Equals(readOnly, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var privileges = response.Find(DavNames.CurrentUserPrivilegeSet);
        if (privileges is null)
        {
            return false;
        }

        var granted = privileges.Elements(DavNames.Privilege).SelectMany(p => p.Elements()).Select(e => e.Name).ToHashSet();
        return !granted.Contains(DavNames.All) && !granted.Contains(DavNames.Write) && !granted.Contains(DavNames.WriteContent);
    }

    private static string LastSegment(string href) =>
        Uri.UnescapeDataString(href.TrimEnd('/').Split('/')[^1]);
}
