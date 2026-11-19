using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Administration;

/// <summary>
/// An entity's shared mailboxes (phase 10) as Keycloak keeps them: subgroups of the organization group
/// <c>mailboxes</c>, beside <c>groups</c>, so neither is ever read as the other. A subgroup's name is the mailbox's
/// address, which never changes (Keycloak refuses a second subgroup with the same name); its members are the people
/// who may open it, and everything else is in admin-only group attributes.
/// </summary>
internal sealed class KeycloakEntityMailboxes(IKeycloakAdminClient keycloak) : IEntityMailboxes
{
    public const string ParentName = OrganizationGroupNames.MailboxesParent;

    internal const string NameAttribute = OrganizationGroupNames.NameAttribute;
    internal const string AliasesAttribute = "nightware_mailbox_aliases";

    public async Task<Result<IReadOnlyList<SharedMailbox>>> ListAsync(string entityId, CancellationToken cancellationToken)
    {
        Result<KeycloakGroup?> parent = await OrganizationParentGroup.FindAsync(keycloak, entityId, ParentName, cancellationToken).ConfigureAwait(false);
        if (!parent.IsSuccess || parent.Value is null)
        {
            return parent.IsSuccess ? Array.Empty<SharedMailbox>() : parent.Error;
        }

        Result<IReadOnlyList<KeycloakGroup>> children = await keycloak.ListOrganizationGroupChildrenAsync(entityId, parent.Value.Id, cancellationToken).ConfigureAwait(false);
        return children.IsSuccess ? await ReadAllAsync(entityId, children.Value, cancellationToken).ConfigureAwait(false) : children.Error;
    }

    public async Task<Result<SharedMailbox>> GetAsync(string entityId, string mailboxId, CancellationToken cancellationToken)
    {
        Result<KeycloakGroup?> parent = await OrganizationParentGroup.FindAsync(keycloak, entityId, ParentName, cancellationToken).ConfigureAwait(false);
        if (!parent.IsSuccess)
        {
            return parent.Error;
        }

        Result<KeycloakGroup> group = parent.Value is null
            ? NotFound()
            : await keycloak.GetOrganizationGroupAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
        if (!group.IsSuccess)
        {
            return group.Error;
        }

        // Only a subgroup of "mailboxes" is a shared mailbox: never a group or "admins", whatever id is asked for.
        return string.Equals(group.Value.ParentId, parent.Value!.Id, StringComparison.Ordinal) && ToMailbox(entityId, group.Value) is { } read ? read : NotFound();
    }

    public async Task<Result<SharedMailbox>> CreateAsync(string entityId, NewSharedMailbox mailbox, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        Result<string> parent = await OrganizationParentGroup.EnsureAsync(keycloak, entityId, ParentName, cancellationToken).ConfigureAwait(false);
        if (!parent.IsSuccess)
        {
            return parent.Error;
        }

        Result<string> created = await keycloak.CreateOrganizationSubGroupAsync(entityId, parent.Value, mailbox.Address.Value, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return created.Error.Kind == ErrorKind.Conflict
                ? Error.Conflict($"The entity already has a shared mailbox at {mailbox.Address}.", "address")
                : created.Error;
        }

        return await UpdateAsync(new SharedMailbox(created.Value, entityId, mailbox.Address, mailbox.Name) { Aliases = mailbox.Aliases }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SharedMailbox>> UpdateAsync(SharedMailbox mailbox, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        Result written = await keycloak.UpdateOrganizationGroupAsync(mailbox.EntityId, mailbox.Id, mailbox.Address.Value, Attributes(mailbox), cancellationToken).ConfigureAwait(false);
        return written.IsSuccess ? mailbox : written.Error;
    }

    public async Task<Result> DeleteAsync(string entityId, string mailboxId, CancellationToken cancellationToken)
    {
        Result<SharedMailbox> mailbox = await GetAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
        return mailbox.IsSuccess
            ? await keycloak.DeleteOrganizationGroupAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false)
            : mailbox.Error;
    }

    public async Task<Result<IReadOnlyList<IdentityAccount>>> ListMembersAsync(string entityId, string mailboxId, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<KeycloakUser>> members = await keycloak.ListOrganizationGroupMembersAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
        return members.IsSuccess ? Result<IReadOnlyList<IdentityAccount>>.Success(KeycloakAdministrationMapper.ToIdentities(members.Value)) : members.Error;
    }

    public Task<Result> AddMemberAsync(string entityId, string mailboxId, string userId, CancellationToken cancellationToken) =>
        keycloak.AddOrganizationGroupMemberAsync(entityId, mailboxId, userId, cancellationToken);

    public async Task<Result> RemoveMemberAsync(string entityId, string mailboxId, string userId, CancellationToken cancellationToken)
    {
        // Keycloak answers 400 to removing a non-member, so look first.
        Result<IReadOnlyList<KeycloakGroup>> mine = await keycloak.ListMemberOrganizationGroupsAsync(entityId, userId, cancellationToken).ConfigureAwait(false);
        if (!mine.IsSuccess)
        {
            return mine.Error.Kind == ErrorKind.NotFound ? Result.Success() : mine.Error;
        }

        return mine.Value.Any(g => string.Equals(g.Id, mailboxId, StringComparison.Ordinal))
            ? await keycloak.RemoveOrganizationGroupMemberAsync(entityId, mailboxId, userId, cancellationToken).ConfigureAwait(false)
            : Result.Success();
    }

    public async Task<Result<IReadOnlyList<SharedMailbox>>> ListUserMailboxesAsync(string entityId, string userId, CancellationToken cancellationToken)
    {
        Result<KeycloakGroup?> parent = await OrganizationParentGroup.FindAsync(keycloak, entityId, ParentName, cancellationToken).ConfigureAwait(false);
        if (!parent.IsSuccess || parent.Value is null)
        {
            return parent.IsSuccess ? Array.Empty<SharedMailbox>() : parent.Error;
        }

        Result<IReadOnlyList<KeycloakGroup>> mine = await keycloak.ListMemberOrganizationGroupsAsync(entityId, userId, cancellationToken).ConfigureAwait(false);
        if (!mine.IsSuccess)
        {
            return mine.Error.Kind == ErrorKind.NotFound ? Array.Empty<SharedMailbox>() : mine.Error;
        }

        string parentId = parent.Value.Id;
        return await ReadAllAsync(entityId, [.. mine.Value.Where(g => string.Equals(g.ParentId, parentId, StringComparison.Ordinal))], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The shared mailbox as Keycloak holds it; null for a subgroup the console did not make (its name is not an address).</summary>
    internal static SharedMailbox? ToMailbox(string entityId, KeycloakGroup group)
    {
        if (!MailAddress.TryParse(group.Name, out MailAddress? address) || !string.Equals(address.Value, group.Name, StringComparison.Ordinal))
        {
            return null;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> attributes = group.Attributes ?? new Dictionary<string, IReadOnlyList<string>>();
        string? name = attributes.TryGetValue(NameAttribute, out IReadOnlyList<string>? names) && names.Count > 0 && names[0].Length > 0 ? names[0] : null;
        IEnumerable<string> aliases = attributes.TryGetValue(AliasesAttribute, out IReadOnlyList<string>? values) ? values : [];
        return new SharedMailbox(group.Id, entityId, address, name ?? address.Value)
        {
            Aliases = [.. aliases.Select(static a => MailAddress.TryParse(a, out MailAddress? alias) ? alias : null).OfType<MailAddress>()],
        };
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes(SharedMailbox mailbox) =>
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [NameAttribute] = [mailbox.Name],
            [AliasesAttribute] = [.. mailbox.Aliases.Select(static a => a.Value)],
        };

    private static Error NotFound() => Error.NotFound("The shared mailbox was not found.");

    private async Task<Result<IReadOnlyList<SharedMailbox>>> ReadAllAsync(string entityId, IReadOnlyList<KeycloakGroup> listed, CancellationToken cancellationToken)
    {
        // Listings leave the attributes out, so each mailbox is read on its own.
        var mailboxes = new List<SharedMailbox>();
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

            if (ToMailbox(entityId, full.Value) is { } mailbox)
            {
                mailboxes.Add(mailbox);
            }
        }

        return mailboxes;
    }
}
