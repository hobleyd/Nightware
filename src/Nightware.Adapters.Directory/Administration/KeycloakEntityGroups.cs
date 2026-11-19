using System.Globalization;

using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Administration;

/// <summary>
/// An entity's groups (phase 9) as Keycloak keeps them: subgroups of the organization group <c>groups</c>, so
/// <c>admins</c> and any later system group are never one. A subgroup's name is the group's immutable key (Keycloak
/// refuses a second subgroup with the same name, which keeps keys unique); everything else is in admin-only group
/// attributes. Organization groups are invisible to the realm's own group API.
/// </summary>
internal sealed class KeycloakEntityGroups(IKeycloakAdminClient keycloak) : IEntityGroups
{
    public const string ParentName = "groups";

    internal const string NameAttribute = "nightware_name";
    internal const string ListAddressAttribute = "nightware_list_address";
    internal const string ListAliasesAttribute = "nightware_list_aliases";
    internal const string ExternalRecipientsAttribute = "nightware_external_recipients";
    internal const string SendersAttribute = "nightware_senders";
    internal const string FilesAttribute = "nightware_files";
    internal const string TeamFolderAttribute = "nightware_team_folder";
    internal const string TeamFolderIdAttribute = "nightware_team_folder_id";

    public async Task<Result<IReadOnlyList<Group>>> ListAsync(string entityId, CancellationToken cancellationToken)
    {
        Result<KeycloakGroup?> parent = await FindParentAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!parent.IsSuccess || parent.Value is null)
        {
            return parent.IsSuccess ? Array.Empty<Group>() : parent.Error;
        }

        Result<IReadOnlyList<KeycloakGroup>> children = await keycloak.ListOrganizationGroupChildrenAsync(entityId, parent.Value.Id, cancellationToken).ConfigureAwait(false);
        return children.IsSuccess ? await ReadAllAsync(entityId, children.Value, cancellationToken).ConfigureAwait(false) : children.Error;
    }

    public async Task<Result<Group>> GetAsync(string entityId, string groupId, CancellationToken cancellationToken)
    {
        Result<KeycloakGroup?> parent = await FindParentAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!parent.IsSuccess)
        {
            return parent.Error;
        }

        Result<KeycloakGroup> group = parent.Value is null
            ? NotFound()
            : await keycloak.GetOrganizationGroupAsync(entityId, groupId, cancellationToken).ConfigureAwait(false);
        if (!group.IsSuccess)
        {
            return group.Error;
        }

        // Only a subgroup of "groups" is a group: never "admins", whatever id is asked for.
        return string.Equals(group.Value.ParentId, parent.Value!.Id, StringComparison.Ordinal) && ToGroup(entityId, group.Value) is { } read ? read : NotFound();
    }

    public async Task<Result<Group>> CreateAsync(string entityId, NewGroup group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        Result<string> parent = await EnsureParentAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!parent.IsSuccess)
        {
            return parent.Error;
        }

        Result<string> created = await keycloak.CreateOrganizationSubGroupAsync(entityId, parent.Value, group.Key.Value, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return created.Error.Kind == ErrorKind.Conflict
                ? Error.Conflict($"The entity already has a group with the key {group.Key}.", "key")
                : created.Error;
        }

        return await UpdateAsync(new Group(created.Value, entityId, group.Key, group.Name) { Mail = group.Mail, Files = group.Files }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<Group>> UpdateAsync(Group group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        Result written = await keycloak.UpdateOrganizationGroupAsync(group.EntityId, group.Id, group.Key.Value, Attributes(group), cancellationToken).ConfigureAwait(false);
        return written.IsSuccess ? group : written.Error;
    }

    public async Task<Result> DeleteAsync(string entityId, string groupId, CancellationToken cancellationToken)
    {
        Result<Group> group = await GetAsync(entityId, groupId, cancellationToken).ConfigureAwait(false);
        return group.IsSuccess
            ? await keycloak.DeleteOrganizationGroupAsync(entityId, groupId, cancellationToken).ConfigureAwait(false)
            : group.Error;
    }

    public async Task<Result<IReadOnlyList<IdentityAccount>>> ListMembersAsync(string entityId, string groupId, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<KeycloakUser>> members = await keycloak.ListOrganizationGroupMembersAsync(entityId, groupId, cancellationToken).ConfigureAwait(false);
        return members.IsSuccess ? Result<IReadOnlyList<IdentityAccount>>.Success(KeycloakAdministrationMapper.ToIdentities(members.Value)) : members.Error;
    }

    public Task<Result> AddMemberAsync(string entityId, string groupId, string userId, CancellationToken cancellationToken) =>
        keycloak.AddOrganizationGroupMemberAsync(entityId, groupId, userId, cancellationToken);

    public async Task<Result> RemoveMemberAsync(string entityId, string groupId, string userId, CancellationToken cancellationToken)
    {
        // Keycloak answers 400 to removing a non-member, so look first.
        Result<IReadOnlyList<KeycloakGroup>> mine = await keycloak.ListMemberOrganizationGroupsAsync(entityId, userId, cancellationToken).ConfigureAwait(false);
        if (!mine.IsSuccess)
        {
            return mine.Error.Kind == ErrorKind.NotFound ? Result.Success() : mine.Error;
        }

        return mine.Value.Any(g => string.Equals(g.Id, groupId, StringComparison.Ordinal))
            ? await keycloak.RemoveOrganizationGroupMemberAsync(entityId, groupId, userId, cancellationToken).ConfigureAwait(false)
            : Result.Success();
    }

    public async Task<Result<IReadOnlyList<Group>>> ListUserGroupsAsync(string entityId, string userId, CancellationToken cancellationToken)
    {
        Result<KeycloakGroup?> parent = await FindParentAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!parent.IsSuccess || parent.Value is null)
        {
            return parent.IsSuccess ? Array.Empty<Group>() : parent.Error;
        }

        Result<IReadOnlyList<KeycloakGroup>> mine = await keycloak.ListMemberOrganizationGroupsAsync(entityId, userId, cancellationToken).ConfigureAwait(false);
        if (!mine.IsSuccess)
        {
            return mine.Error.Kind == ErrorKind.NotFound ? Array.Empty<Group>() : mine.Error;
        }

        string parentId = parent.Value.Id;
        return await ReadAllAsync(entityId, [.. mine.Value.Where(g => string.Equals(g.ParentId, parentId, StringComparison.Ordinal))], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The group as Keycloak holds it; null for a subgroup the console did not make (its name is not a key).</summary>
    internal static Group? ToGroup(string entityId, KeycloakGroup group)
    {
        if (!GroupKey.TryParse(group.Name, out GroupKey? key) || !string.Equals(key.Value, group.Name, StringComparison.Ordinal))
        {
            return null;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> attributes = group.Attributes ?? new Dictionary<string, IReadOnlyList<string>>();
        GroupMail? mail = null;
        if (MailAddress.TryParse(First(attributes, ListAddressAttribute), out MailAddress? address))
        {
            mail = new GroupMail(address, Enum.TryParse(First(attributes, SendersAttribute), true, out SenderPolicy senders) && Enum.IsDefined(senders) ? senders : SenderPolicy.Members)
            {
                Aliases = [.. All(attributes, ListAliasesAttribute).Select(static a => MailAddress.TryParse(a, out MailAddress? alias) ? alias : null).OfType<MailAddress>()],
                ExternalRecipients = [.. All(attributes, ExternalRecipientsAttribute).Select(static r => RecipientAddress.TryParse(r, out RecipientAddress? recipient) ? recipient : null).OfType<RecipientAddress>()],
            };
        }

        GroupFiles? files = null;
        if (IsTrue(First(attributes, FilesAttribute)))
        {
            files = new GroupFiles(IsTrue(First(attributes, TeamFolderAttribute)))
            {
                TeamFolderId = int.TryParse(First(attributes, TeamFolderIdAttribute), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : null,
            };
        }

        return new Group(group.Id, entityId, key, First(attributes, NameAttribute) is { Length: > 0 } name ? name : key.Value) { Mail = mail, Files = files };
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes(Group group)
    {
        var attributes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [NameAttribute] = [group.Name] };
        if (group.Mail is { } mail)
        {
            attributes[ListAddressAttribute] = [mail.Address.Value];
            attributes[SendersAttribute] = [mail.Senders.ToString().ToLowerInvariant()];
            attributes[ListAliasesAttribute] = [.. mail.Aliases.Select(static a => a.Value)];
            attributes[ExternalRecipientsAttribute] = [.. mail.ExternalRecipients.Select(static r => r.Value)];
        }

        if (group.Files is { } files)
        {
            attributes[FilesAttribute] = ["true"];
            attributes[TeamFolderAttribute] = [files.TeamFolder ? "true" : "false"];
            if (files.TeamFolderId is { } id)
            {
                attributes[TeamFolderIdAttribute] = [id.ToString(CultureInfo.InvariantCulture)];
            }
        }

        return attributes;
    }

    private async Task<Result<IReadOnlyList<Group>>> ReadAllAsync(string entityId, IReadOnlyList<KeycloakGroup> listed, CancellationToken cancellationToken)
    {
        // Listings leave the attributes out, so each group is read on its own.
        var groups = new List<Group>();
        foreach (KeycloakGroup child in listed)
        {
            Result<KeycloakGroup> full = await keycloak.GetOrganizationGroupAsync(entityId, child.Id, cancellationToken).ConfigureAwait(false);
            if (!full.IsSuccess && full.Error.Kind == ErrorKind.NotFound)
            {
                continue;
            }

            if (!full.IsSuccess)
            {
                return full.Error;
            }

            if (ToGroup(entityId, full.Value) is { } group)
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    private Task<Result<KeycloakGroup?>> FindParentAsync(string entityId, CancellationToken cancellationToken) =>
        OrganizationParentGroup.FindAsync(keycloak, entityId, ParentName, cancellationToken);

    private Task<Result<string>> EnsureParentAsync(string entityId, CancellationToken cancellationToken) =>
        OrganizationParentGroup.EnsureAsync(keycloak, entityId, ParentName, cancellationToken);

    private static Error NotFound() => Error.NotFound("The group was not found.");

    private static string? First(IReadOnlyDictionary<string, IReadOnlyList<string>> attributes, string name) =>
        attributes.TryGetValue(name, out IReadOnlyList<string>? values) && values.Count > 0 ? values[0] : null;

    private static IEnumerable<string> All(IReadOnlyDictionary<string, IReadOnlyList<string>> attributes, string name) =>
        attributes.TryGetValue(name, out IReadOnlyList<string>? values) ? values.Where(static v => !string.IsNullOrWhiteSpace(v)) : [];

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
