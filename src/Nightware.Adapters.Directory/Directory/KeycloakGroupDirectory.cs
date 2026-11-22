using Nightware.Adapters.Directory.Administration;
using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Application.Common.Identity;
using Nightware.Application.Common.Query;
using Nightware.Application.Directory;
using Nightware.Domain.Administration;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Directory;

/// <summary>
/// <see cref="IGroupDirectory"/> over the groups the console keeps (ADR 0003): the subgroups of each organization's
/// <c>groups</c> group, read as the <c>nightware-api</c> service account (realm-management <c>view-organizations</c>), for
/// the organizations the signed-in user belongs to only. A subgroup the console did not make (its name is no key) is
/// skipped, as the console skips it; shared mailboxes and <c>admins</c> are never groups.
/// </summary>
/// <remarks>
/// Listings leave the attributes out, so a page is sliced from the brief listing first and only its groups are read
/// whole. The <c>$skiptoken</c> is the next offset into the caller's groups.
/// </remarks>
internal sealed class KeycloakGroupDirectory(IKeycloakAdminClient keycloak, IUserContext user) : IGroupDirectory
{
    public async Task<Result<Page<DirectoryGroup>>> ListAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        var offset = DirectoryPaging.Offset(query, "groups");
        if (!offset.IsSuccess)
        {
            return offset.Error;
        }

        var organizations = await CallerOrganizationsAsync(cancellationToken).ConfigureAwait(false);
        if (!organizations.IsSuccess)
        {
            return organizations.Error;
        }

        var listed = new List<(string OrganizationId, KeycloakGroup Group)>();
        foreach (var organizationId in organizations.Value)
        {
            var parent = await GroupsParentAsync(organizationId, cancellationToken).ConfigureAwait(false);
            if (!parent.IsSuccess)
            {
                return parent.Error;
            }

            if (parent.Value is not { } parentId)
            {
                continue;
            }

            var children = await keycloak.ListOrganizationGroupChildrenAsync(organizationId, parentId, cancellationToken).ConfigureAwait(false);
            if (!children.IsSuccess)
            {
                return children.Error;
            }

            listed.AddRange(children.Value.Select(c => (organizationId, c)));
        }

        var page = DirectoryPaging.Slice(listed, offset.Value, DirectoryPaging.Top(query));
        var groups = new List<DirectoryGroup>();
        foreach (var (organizationId, child) in page.Items)
        {
            var full = await keycloak.GetOrganizationGroupAsync(organizationId, child.Id, cancellationToken).ConfigureAwait(false);
            if (!full.IsSuccess && full.Error.Kind == ErrorKind.NotFound)
            {
                continue; // deleted mid-read
            }

            if (!full.IsSuccess)
            {
                return full.Error;
            }

            if (KeycloakEntityGroups.ToGroup(organizationId, full.Value) is { } group)
            {
                groups.Add(ToDirectoryGroup(group));
            }
        }

        return new Page<DirectoryGroup>(groups, page.SkipToken);
    }

    public async Task<Result<DirectoryGroup>> GetAsync(string groupId, CancellationToken cancellationToken)
    {
        var found = await FindAsync(groupId, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess ? ToDirectoryGroup(found.Value) : found.Error;
    }

    public async Task<Result<Page<DirectoryUser>>> ListMembersAsync(string groupId, ItemQuery query, CancellationToken cancellationToken)
    {
        var offset = DirectoryPaging.Offset(query, "members");
        if (!offset.IsSuccess)
        {
            return offset.Error;
        }

        var found = await FindAsync(groupId, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var members = await keycloak.ListOrganizationGroupMembersAsync(found.Value.EntityId, found.Value.Id, cancellationToken).ConfigureAwait(false);
        if (!members.IsSuccess)
        {
            return members.Error;
        }

        IReadOnlyList<DirectoryUser> people = [.. members.Value
            .Where(static u => u.Enabled && !KeycloakUserMapper.IsServiceAccount(u))
            .Select(KeycloakUserMapper.ToDirectoryUser)];
        return DirectoryPaging.Slice(people, offset.Value, DirectoryPaging.Top(query));
    }

    public async Task<Result<Page<DirectoryGroup>>> ListMemberOfAsync(string userId, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var offset = DirectoryPaging.Offset(query, "memberOf");
        if (!offset.IsSuccess)
        {
            return offset.Error;
        }

        var organizations = await CallerOrganizationsAsync(cancellationToken).ConfigureAwait(false);
        if (!organizations.IsSuccess)
        {
            return organizations.Error;
        }

        // Another user's groups only in the organizations the caller shares; ListUserGroupsAsync is empty for a non-member.
        var groups = new KeycloakEntityGroups(keycloak);
        var all = new List<DirectoryGroup>();
        foreach (var organizationId in organizations.Value)
        {
            var mine = await groups.ListUserGroupsAsync(organizationId, userId, cancellationToken).ConfigureAwait(false);
            if (!mine.IsSuccess)
            {
                return mine.Error;
            }

            all.AddRange(mine.Value.Select(ToDirectoryGroup));
        }

        return DirectoryPaging.Slice(all, offset.Value, DirectoryPaging.Top(query));
    }

    internal static DirectoryGroup ToDirectoryGroup(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new DirectoryGroup(group.Id, group.Name, group.Key.Value)
        {
            Mail = group.Mail?.Address.Value,
            Aliases = group.Mail is { } mail ? [.. mail.Aliases.Select(static a => a.Value)] : [],
            SecurityEnabled = group.Files is not null,
        };
    }

    /// <summary>The group with <paramref name="groupId"/> among the caller's organizations' groups, or NotFound.</summary>
    private async Task<Result<Group>> FindAsync(string groupId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        if (!Guid.TryParse(groupId, out _))
        {
            return NotFound(groupId);
        }

        var organizations = await CallerOrganizationsAsync(cancellationToken).ConfigureAwait(false);
        if (!organizations.IsSuccess)
        {
            return organizations.Error;
        }

        foreach (var organizationId in organizations.Value)
        {
            var parent = await GroupsParentAsync(organizationId, cancellationToken).ConfigureAwait(false);
            if (!parent.IsSuccess)
            {
                return parent.Error;
            }

            if (parent.Value is not { } parentId)
            {
                continue;
            }

            var group = await keycloak.GetOrganizationGroupAsync(organizationId, groupId, cancellationToken).ConfigureAwait(false);
            if (!group.IsSuccess && group.Error.Kind == ErrorKind.NotFound)
            {
                continue;
            }

            if (!group.IsSuccess)
            {
                return group.Error;
            }

            // Only a subgroup of "groups" is a group: never "admins" or a shared mailbox, whatever id is asked for.
            if (string.Equals(group.Value.ParentId, parentId, StringComparison.Ordinal) && KeycloakEntityGroups.ToGroup(organizationId, group.Value) is { } found)
            {
                return found;
            }
        }

        return NotFound(groupId);
    }

    private async Task<Result<IReadOnlyList<string>>> CallerOrganizationsAsync(CancellationToken cancellationToken)
    {
        var organizations = await keycloak.ListMemberOrganizationsAsync(user.Principal.Id, cancellationToken).ConfigureAwait(false);
        if (!organizations.IsSuccess)
        {
            return organizations.Error.Kind == ErrorKind.NotFound ? Array.Empty<string>() : organizations.Error;
        }

        return organizations.Value.Where(static o => o.Enabled).Select(static o => o.Id).OfType<string>().ToList();
    }

    private async Task<Result<string?>> GroupsParentAsync(string organizationId, CancellationToken cancellationToken)
    {
        var parent = await OrganizationParentGroup.FindAsync(keycloak, organizationId, OrganizationGroupNames.GroupsParent, cancellationToken).ConfigureAwait(false);
        return parent.IsSuccess ? parent.Value?.Id : parent.Error;
    }

    private static Error NotFound(string groupId) => Error.NotFound($"No group '{groupId}' in your organization.");
}
