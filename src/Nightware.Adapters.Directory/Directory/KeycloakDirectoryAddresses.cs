using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Domain.Administration;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Directory;

/// <summary>
/// <see cref="IDirectoryAddresses"/> from the organization groups the console keeps (ADR 0003, ADR 0004), read as the
/// <c>nightware-api</c> service account (realm-management <c>view-organizations</c>), for the organizations the signed-in
/// user belongs to only. A group with no list is skipped, and a members-only list is offered to its members only, since
/// the mail server refuses anyone else; every shared mailbox is offered (its address is for mailing, like any user's).
/// </summary>
/// <remarks>
/// Listings leave the attributes out, so each group is read on its own, as the console does. A group or mailbox the
/// console did not make (its name is no key or address) is skipped, and one deleted mid-read is ignored.
/// </remarks>
internal sealed class KeycloakDirectoryAddresses(IKeycloakAdminClient keycloak, IUserContext user) : IDirectoryAddresses
{
    public async Task<Result<IReadOnlyList<DirectoryAddress>>> ListAsync(CancellationToken cancellationToken)
    {
        string userId = user.Principal.Id;
        Result<IReadOnlyList<KeycloakOrganization>> organizations = await keycloak.ListMemberOrganizationsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!organizations.IsSuccess)
        {
            return organizations.Error;
        }

        var addresses = new List<DirectoryAddress>();
        foreach (KeycloakOrganization organization in organizations.Value)
        {
            if (organization.Id is not { } organizationId)
            {
                continue;
            }

            Result read = await ReadOrganizationAsync(organizationId, userId, addresses, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                return read.Error;
            }
        }

        return addresses.DistinctBy(static a => a.Address, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<Result> ReadOrganizationAsync(string organizationId, string userId, List<DirectoryAddress> addresses, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<KeycloakGroup>> top = await keycloak.ListOrganizationTopLevelGroupsAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (!top.IsSuccess)
        {
            return top.Error;
        }

        if (Parent(top.Value, OrganizationGroupNames.GroupsParent) is { } groupsParent)
        {
            Result<IReadOnlyList<KeycloakGroup>> mine = await keycloak.ListMemberOrganizationGroupsAsync(organizationId, userId, cancellationToken).ConfigureAwait(false);
            if (!mine.IsSuccess && mine.Error.Kind != ErrorKind.NotFound)
            {
                return mine.Error;
            }

            HashSet<string> memberOf = mine.IsSuccess ? [.. mine.Value.Select(static g => g.Id)] : [];
            Result groups = await ReadChildrenAsync(organizationId, groupsParent, g => ToListAddress(g, memberOf), addresses, cancellationToken).ConfigureAwait(false);
            if (!groups.IsSuccess)
            {
                return groups;
            }
        }

        return Parent(top.Value, OrganizationGroupNames.MailboxesParent) is { } mailboxesParent
            ? await ReadChildrenAsync(organizationId, mailboxesParent, ToMailboxAddress, addresses, cancellationToken).ConfigureAwait(false)
            : Result.Success();
    }

    private async Task<Result> ReadChildrenAsync(
        string organizationId,
        string parentId,
        Func<KeycloakGroup, DirectoryAddress?> map,
        List<DirectoryAddress> addresses,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<KeycloakGroup>> children = await keycloak.ListOrganizationGroupChildrenAsync(organizationId, parentId, cancellationToken).ConfigureAwait(false);
        if (!children.IsSuccess)
        {
            return children.Error;
        }

        foreach (KeycloakGroup child in children.Value)
        {
            Result<KeycloakGroup> full = await keycloak.GetOrganizationGroupAsync(organizationId, child.Id, cancellationToken).ConfigureAwait(false);
            if (!full.IsSuccess && full.Error.Kind == ErrorKind.NotFound)
            {
                continue;
            }

            if (!full.IsSuccess)
            {
                return full.Error;
            }

            if (map(full.Value) is { } address)
            {
                addresses.Add(address);
            }
        }

        return Result.Success();
    }

    /// <summary>A group's list, when it has one and the user may send to it.</summary>
    internal static DirectoryAddress? ToListAddress(KeycloakGroup group, IReadOnlySet<string> memberOf)
    {
        if (!GroupKey.TryParse(group.Name, out GroupKey? key) || !string.Equals(key.Value, group.Name, StringComparison.Ordinal)
            || !MailAddress.TryParse(First(group, OrganizationGroupNames.ListAddressAttribute), out MailAddress? address))
        {
            return null;
        }

        // As the console reads it: anything but organization or anyone is members-only.
        bool membersOnly = !Enum.TryParse(First(group, OrganizationGroupNames.SendersAttribute), true, out SenderPolicy senders)
            || !Enum.IsDefined(senders) || senders == SenderPolicy.Members;
        return membersOnly && !memberOf.Contains(group.Id) ? null : new DirectoryAddress(address.Value, Name(group) ?? key.Value);
    }

    /// <summary>A shared mailbox, whose subgroup name is its address.</summary>
    internal static DirectoryAddress? ToMailboxAddress(KeycloakGroup group) =>
        MailAddress.TryParse(group.Name, out MailAddress? address) && string.Equals(address.Value, group.Name, StringComparison.Ordinal)
            ? new DirectoryAddress(address.Value, Name(group) ?? address.Value)
            : null;

    private static string? Parent(IReadOnlyList<KeycloakGroup> top, string name) =>
        top.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.Ordinal))?.Id;

    private static string? Name(KeycloakGroup group) => First(group, OrganizationGroupNames.NameAttribute) is { Length: > 0 } name ? name.Trim() : null;

    private static string? First(KeycloakGroup group, string attribute) =>
        group.Attributes is { } attributes && attributes.TryGetValue(attribute, out IReadOnlyList<string>? values) && values.Count > 0 ? values[0] : null;
}
