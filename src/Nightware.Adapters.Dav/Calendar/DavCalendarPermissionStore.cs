using System.Xml.Linq;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Application.Calendar.Calendars;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// Calendar sharing over Nextcloud's CalDAV extension (ADR 0006 D6): sharees are read from <c>oc:invite</c>, and
/// <c>POST oc:share</c> sets (<c>oc:read-write</c> for write) or removes one. A permission's id is the sharee's
/// principal, minted like every DAV id; only the calendar's owner may share it.
/// </summary>
internal sealed class DavCalendarPermissionStore(DavCalendarStore calendars, IDavPrincipalDiscovery discovery, IDavClient dav, DavPaths paths) : ICalendarPermissionStore
{
    private const string PrincipalPrefix = "principal:";

    public async Task<Result<IReadOnlyList<CalendarShare>>> ListAsync(MailboxRef mailbox, CalendarId calendar, CancellationToken cancellationToken)
    {
        var resolved = await calendars.ResolveAsync(mailbox, calendar, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return resolved.Error;
        }

        var invite = await dav.PropFindAsync(resolved.Value.Calendar.Href, DavDepth.Zero, [DavNames.OwnCloudInvite], cancellationToken).ConfigureAwait(false);
        if (!invite.IsSuccess)
        {
            return invite.Error;
        }

        var permissions = new List<CalendarShare>();
        foreach (var user in invite.Value.Responses.SelectMany(static r => r.Find(DavNames.OwnCloudInvite)?.Elements(DavNs.OwnCloud + "user") ?? []))
        {
            var principal = user.Element(DavNames.Href)?.Value is { } href && href.StartsWith(PrincipalPrefix, StringComparison.Ordinal) ? href[PrincipalPrefix.Length..].Trim('/') : null;
            if (principal is null)
            {
                continue;
            }

            var address = await AddressOfAsync(principal, cancellationToken).ConfigureAwait(false);
            var name = user.Element(DavNs.OwnCloud + "common-name")?.Value is { Length: > 0 } common ? common : address ?? principal;
            var write = user.Element(DavNs.OwnCloud + "access")?.Element(DavNs.OwnCloud + "read-write") is not null;
            permissions.Add(new CalendarShare(IdOf(principal), new EmailAddress(address ?? string.Empty, name), write ? CalendarRole.Write : CalendarRole.Read));
        }

        return permissions;
    }

    public async Task<Result<CalendarShare>> GrantAsync(MailboxRef mailbox, CalendarId calendar, string address, CalendarRole role, CancellationToken cancellationToken)
    {
        var resolved = await calendars.ResolveAsync(mailbox, calendar, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return resolved.Error;
        }

        if (!resolved.Value.Scope.Owner.IsCurrentUser || resolved.Value.Scope.SharedWithMe.Contains(resolved.Value.Calendar))
        {
            return Error.Forbidden("Only the calendar's owner can share it.");
        }

        var sharee = await discovery.DiscoverAsync(new MailboxRef(address.ToLowerInvariant()), cancellationToken).ConfigureAwait(false);
        if (!sharee.IsSuccess || paths.ToRelative(sharee.Value.Href)?.Trim('/') is not { } principal || !principal.StartsWith("principals/users/", StringComparison.Ordinal))
        {
            return Error.Validation($"'{address}' is not a user of this server.", "emailAddress");
        }

        var set = new XElement(DavNs.OwnCloud + "set", new XElement(DavNames.Href, PrincipalPrefix + principal), role == CalendarRole.Write ? new XElement(DavNs.OwnCloud + "read-write") : null);
        var shared = await dav.PostXmlAsync(resolved.Value.Calendar.Href, Share(set), cancellationToken).ConfigureAwait(false);
        return shared.IsSuccess
            ? new CalendarShare(IdOf(principal), new EmailAddress(address, sharee.Value.DisplayName ?? address), role)
            : shared.Error;
    }

    public async Task<Result> RevokeAsync(MailboxRef mailbox, CalendarId calendar, string permissionId, CancellationToken cancellationToken)
    {
        var existing = await ListAsync(mailbox, calendar, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return existing.Error;
        }

        if (!existing.Value.Any(p => p.Id == permissionId) || DavItemId.Parse(permissionId, "id") is not { IsSuccess: true } id)
        {
            return Error.NotFound("The permission does not exist.");
        }

        var resolved = await calendars.ResolveAsync(mailbox, calendar, cancellationToken).ConfigureAwait(false);
        return resolved.IsSuccess
            ? await dav.PostXmlAsync(resolved.Value.Calendar.Href, Share(new XElement(DavNs.OwnCloud + "remove", new XElement(DavNames.Href, PrincipalPrefix + id.Value.Href.Trim('/')))), cancellationToken).ConfigureAwait(false)
            : resolved.Error;
    }

    private static XDocument Share(XElement change) =>
        new(new XElement(DavNs.OwnCloud + "share", new XAttribute(XNamespace.Xmlns + "d", DavNs.Dav), new XAttribute(XNamespace.Xmlns + "oc", DavNs.OwnCloud), change));

    private static string IdOf(string principal) => DavItemId.Resource(principal + "/").ToString();

    private async Task<string?> AddressOfAsync(string principal, CancellationToken cancellationToken)
    {
        var found = await dav.PropFindAsync(principal + "/", DavDepth.Zero, [DavNames.CalendarUserAddressSet], cancellationToken).ConfigureAwait(false);
        return found.IsSuccess
            ? found.Value.Responses.SelectMany(static r => r.HrefsOf(DavNames.CalendarUserAddressSet))
                .FirstOrDefault(static h => h.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))?[7..]
            : null;
    }
}
