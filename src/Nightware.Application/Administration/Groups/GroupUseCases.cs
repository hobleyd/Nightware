using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Groups;

/// <summary>An entity's groups, by name.</summary>
public sealed class ListGroups(GroupAccess access, IEntityGroups groups)
{
    public async Task<Result<(Entity Entity, IReadOnlyList<Group> Groups)>> ExecuteAsync(string entityId, CancellationToken cancellationToken)
    {
        Result<Entity> entity = await access.LoadEntityAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess)
        {
            return entity.Error;
        }

        Result<IReadOnlyList<Group>> listed = await groups.ListAsync(entityId, cancellationToken).ConfigureAwait(false);
        return listed.IsSuccess
            ? (entity.Value, [.. listed.Value.OrderBy(static g => g.Name, StringComparer.CurrentCultureIgnoreCase)])
            : listed.Error;
    }
}

/// <summary>One group with its members and its state in each backend.</summary>
public sealed class GetGroup(GroupAccess access)
{
    public Task<Result<GroupDetail>> ExecuteAsync(string entityId, string groupId, CancellationToken cancellationToken) =>
        access.LoadDetailAsync(entityId, groupId, cancellationToken);
}

/// <summary>The groups a user belongs to, in the entities the actor manages.</summary>
public sealed class ListUserGroups(IAdminActor actor, UserAccess users, IEntityRegistry entities, IEntityGroups groups)
{
    public async Task<Result<IReadOnlyList<Group>>> ExecuteAsync(string userId, CancellationToken cancellationToken)
    {
        Result<ManagedUser> user = await users.LoadAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess)
        {
            return user.Error;
        }

        Result<IReadOnlyList<string>> memberships = await entities.GetMembershipsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!memberships.IsSuccess)
        {
            return memberships.Error;
        }

        var found = new List<Group>();
        foreach (string entityId in memberships.Value.Where(actor.Current.MayManage))
        {
            Result<IReadOnlyList<Group>> mine = await groups.ListUserGroupsAsync(entityId, userId, cancellationToken).ConfigureAwait(false);
            if (!mine.IsSuccess)
            {
                return mine.Error;
            }

            found.AddRange(mine.Value);
        }

        return found.OrderBy(static g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

/// <summary>
/// Creates a group in the record, then builds its list and Nextcloud group. A backend that fails leaves the group
/// created with that backend Missing, for Repair.
/// </summary>
public sealed class CreateGroup(IAdminActor actor, GroupAccess access, IEntityGroups groups, GroupSync sync, IAuditLog audit)
{
    public async Task<Result<GroupSyncOutcome>> ExecuteAsync(string entityId, GroupForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        Result<Entity> entity = await access.LoadEntityAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess)
        {
            return entity.Error;
        }

        Result<(string Name, GroupMail? Mail, GroupFiles? Files)> read = await access.ReadAsync(entity.Value, form, null, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        GroupKey? key = string.IsNullOrWhiteSpace(form.Key) ? GroupKey.Propose(form.Name) : GroupKey.TryParse(form.Key, out GroupKey? typed) ? typed : null;
        if (key is null)
        {
            return Error.Validation("Give the group a key: lower-case letters, digits and hyphens, starting with a letter.", "key");
        }

        Result<Group> created = await groups.CreateAsync(entityId, new NewGroup(key, read.Value.Name, read.Value.Mail, read.Value.Files), cancellationToken).ConfigureAwait(false);
        Result<GroupSyncOutcome> outcome = created.IsSuccess
            ? await sync.ApplyAsync(entity.Value, created.Value, [], cancellationToken).ConfigureAwait(false)
            : created.Error;
        audit.Record(new AuditEvent("group.created", actor.Current.UserId, outcome.IsSuccess)
        {
            EntityId = entityId,
            Details = Details(created.IsSuccess ? created.Value : null, key, read.Value.Mail),
        });
        return outcome;
    }

    internal static Dictionary<string, string?> Details(Group? group, GroupKey key, GroupMail? mail) => new()
    {
        ["groupId"] = group?.Id,
        ["key"] = key.Value,
        ["address"] = mail?.Address.Value,
    };
}

/// <summary>
/// Changes a group's name and facets and brings the backends into line. Turning the mail facet off destroys the
/// list; turning the files facet off deletes the Nextcloud group (refused while the group has a team folder).
/// </summary>
public sealed class UpdateGroup(IAdminActor actor, GroupAccess access, IEntityGroups groups, IMailingLists lists, IGroupwareGroups groupware, GroupSync sync, IAuditLog audit)
{
    public async Task<Result<GroupSyncOutcome>> ExecuteAsync(string entityId, string groupId, GroupForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        Result<(Entity Entity, Group Group)> loaded = await access.LoadAsync(entityId, groupId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        (Entity entity, Group before) = loaded.Value;
        Result<(string Name, GroupMail? Mail, GroupFiles? Files)> read = await access.ReadAsync(entity, form, before, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        Result<Group> updated = await groups.UpdateAsync(before with { Name = read.Value.Name, Mail = read.Value.Mail, Files = read.Value.Files }, cancellationToken).ConfigureAwait(false);
        if (!updated.IsSuccess)
        {
            return updated.Error;
        }

        Result removed = await GroupRemoval.RemoveFacetsAsync(entity, before, updated.Value, lists, groupware, cancellationToken).ConfigureAwait(false);
        Result<IReadOnlyList<IdentityAccount>> members = await groups.ListMembersAsync(entityId, groupId, cancellationToken).ConfigureAwait(false);
        Result<GroupSyncOutcome> outcome = members.IsSuccess
            ? await sync.ApplyAsync(entity, updated.Value, members.Value, cancellationToken).ConfigureAwait(false)
            : members.Error;
        if (outcome.IsSuccess && !removed.IsSuccess)
        {
            outcome = removed.Error;
        }

        audit.Record(new AuditEvent("group.updated", actor.Current.UserId, outcome.IsSuccess)
        {
            EntityId = entityId,
            Details = CreateGroup.Details(updated.Value, before.Key, updated.Value.Mail ?? before.Mail),
        });
        return outcome;
    }
}

/// <summary>Adds a member of the entity to one of its groups, then brings the backends into line.</summary>
public sealed class AddGroupMember(IAdminActor actor, GroupAccess access, IEntityRegistry entities, IEntityGroups groups, GroupSync sync, IAuditLog audit)
{
    public async Task<Result<GroupSyncOutcome>> ExecuteAsync(string entityId, string groupId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result<(Entity Entity, Group Group)> loaded = await access.LoadAsync(entityId, groupId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        Result<IReadOnlyList<string>> memberships = await entities.GetMembershipsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!memberships.IsSuccess)
        {
            return memberships.Error;
        }

        if (!memberships.Value.Contains(entityId, StringComparer.Ordinal))
        {
            return Error.Validation($"Only a user of {loaded.Value.Entity.Name} can be in its groups.", "userId");
        }

        Result added = await groups.AddMemberAsync(entityId, groupId, userId, cancellationToken).ConfigureAwait(false);
        Result<GroupSyncOutcome> outcome = added.IsSuccess
            ? await GroupMembership.SyncAsync(groups, sync, loaded.Value.Entity, loaded.Value.Group, cancellationToken).ConfigureAwait(false)
            : added.Error;
        audit.Record(new AuditEvent("group.member.added", actor.Current.UserId, outcome.IsSuccess)
        {
            EntityId = entityId,
            TargetUserId = userId,
            Details = new Dictionary<string, string?> { ["groupId"] = groupId },
        });
        return outcome;
    }
}

/// <summary>Takes a user out of a group, then brings the backends into line.</summary>
public sealed class RemoveGroupMember(IAdminActor actor, GroupAccess access, IEntityGroups groups, GroupSync sync, IAuditLog audit)
{
    public async Task<Result<GroupSyncOutcome>> ExecuteAsync(string entityId, string groupId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result<(Entity Entity, Group Group)> loaded = await access.LoadAsync(entityId, groupId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        Result removed = await groups.RemoveMemberAsync(entityId, groupId, userId, cancellationToken).ConfigureAwait(false);
        Result<GroupSyncOutcome> outcome = removed.IsSuccess
            ? await GroupMembership.SyncAsync(groups, sync, loaded.Value.Entity, loaded.Value.Group, cancellationToken).ConfigureAwait(false)
            : removed.Error;
        audit.Record(new AuditEvent("group.member.removed", actor.Current.UserId, outcome.IsSuccess)
        {
            EntityId = entityId,
            TargetUserId = userId,
            Details = new Dictionary<string, string?> { ["groupId"] = groupId },
        });
        return outcome;
    }
}

/// <summary>Brings a group's list and Nextcloud group back into line with the record.</summary>
public sealed class RepairGroup(IAdminActor actor, GroupAccess access, IEntityGroups groups, GroupSync sync, IAuditLog audit)
{
    public async Task<Result<GroupSyncOutcome>> ExecuteAsync(string entityId, string groupId, CancellationToken cancellationToken)
    {
        Result<(Entity Entity, Group Group)> loaded = await access.LoadAsync(entityId, groupId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        Result<GroupSyncOutcome> outcome = await GroupMembership.SyncAsync(groups, sync, loaded.Value.Entity, loaded.Value.Group, cancellationToken).ConfigureAwait(false);
        audit.Record(new AuditEvent("group.repaired", actor.Current.UserId, outcome.IsSuccess)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?> { ["groupId"] = groupId },
        });
        return outcome;
    }
}

internal static class GroupMembership
{
    public static async Task<Result<GroupSyncOutcome>> SyncAsync(IEntityGroups groups, GroupSync sync, Entity entity, Group group, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<IdentityAccount>> members = await groups.ListMembersAsync(entity.Id, group.Id, cancellationToken).ConfigureAwait(false);
        return members.IsSuccess ? await sync.ApplyAsync(entity, group, members.Value, cancellationToken).ConfigureAwait(false) : members.Error;
    }
}
