using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Groups;

/// <summary>
/// Deletes a group: its list (and the list's sender rule), its Nextcloud group, then the record, last, so a failure
/// leaves the group listed where deleting it can be tried again. A team folder is kept, shared with no one
/// (Nextcloud administrators still see it); deleting its files is <see cref="DeleteTeamFolder"/>.
/// </summary>
public sealed class DeleteGroup(IAdminActor actor, GroupAccess access, IEntityGroups groups, IMailingLists lists, IGroupwareGroups groupware, IAuditLog audit)
{
    public async Task<Result> ExecuteAsync(string entityId, string groupId, CancellationToken cancellationToken)
    {
        Result<(Entity Entity, Group Group)> loaded = await access.LoadAsync(entityId, groupId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        (Entity entity, Group group) = loaded.Value;
        Result removed = await GroupRemoval.RemoveFacetsAsync(entity, group, group with { Mail = null, Files = null }, lists, groupware, cancellationToken).ConfigureAwait(false);
        if (removed.IsSuccess)
        {
            removed = await groups.DeleteAsync(entityId, groupId, cancellationToken).ConfigureAwait(false) is { IsSuccess: false, Error.Kind: not ErrorKind.NotFound } failed
                ? failed
                : Result.Success();
        }

        audit.Record(new AuditEvent("group.deleted", actor.Current.UserId, removed.IsSuccess)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?>
            {
                ["groupId"] = groupId,
                ["key"] = group.Key.Value,
                ["address"] = group.Mail?.Address.Value,
                ["teamFolderKept"] = group.Files?.TeamFolderId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        });
        return removed;
    }
}

/// <summary>
/// Deletes a group's team folder and every file in it. Global admins only (or entity admins where the deployment
/// lets them delete data), behind a fresh sign-in and the group's name typed again, like deleting a user's data.
/// </summary>
public sealed class DeleteTeamFolder(IAdminActor actor, GroupAccess access, IEntityGroups groups, IGroupwareGroups groupware, IAuditLog audit, AdministrationOptions options, TimeProvider time)
{
    public async Task<Result<Group>> ExecuteAsync(string entityId, string groupId, string? typedName, CancellationToken cancellationToken)
    {
        Result<(Entity Entity, Group Group)> loaded = await access.LoadAsync(entityId, groupId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        Group group = loaded.Value.Group;
        Result allowed = Check(group, typedName);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        Result deleted = group.Files!.TeamFolderId is { } folderId
            ? await groupware.DeleteFolderAsync(folderId, cancellationToken).ConfigureAwait(false)
            : Result.Success();
        bool absent = !deleted.IsSuccess && deleted.Error.Kind == ErrorKind.NotFound;
        Result<Group> updated = deleted.IsSuccess || absent
            ? await groups.UpdateAsync(group with { Files = new GroupFiles(TeamFolder: false) }, cancellationToken).ConfigureAwait(false)
            : deleted.Error!;
        audit.Record(new AuditEvent("group.folder.deleted", actor.Current.UserId, updated.IsSuccess)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?>
            {
                ["groupId"] = groupId,
                ["folderId"] = group.Files.TeamFolderId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["folder"] = absent ? "absent" : deleted.IsSuccess ? "deleted" : "failed",
            },
        });
        return updated;
    }

    /// <summary>Whether the actor may delete the group's team folder now; the page asks before showing the confirmation.</summary>
    public Result Check(Group group, string? typedName)
    {
        ArgumentNullException.ThrowIfNull(group);
        Result allowed = AdminAuthorization.RequireMayDeleteData(actor.Current, group.EntityId, options);
        if (allowed.IsSuccess && group.Files is not { TeamFolder: true })
        {
            allowed = Error.Validation("This group has no team folder.");
        }

        if (allowed.IsSuccess)
        {
            allowed = AdminAuthorization.RequireFreshAuthentication(actor.Current, time.GetUtcNow(), options);
        }

        if (allowed.IsSuccess && typedName is not null && !string.Equals(typedName.Trim(), group.Name, StringComparison.OrdinalIgnoreCase))
        {
            allowed = Error.Validation("The name you typed does not match the group's name.", "confirmation");
        }

        return allowed;
    }
}

internal static class GroupRemoval
{
    /// <summary>Takes down what <paramref name="before"/> had and <paramref name="after"/> no longer asks for. Absent is done.</summary>
    public static async Task<Result> RemoveFacetsAsync(Entity entity, Group before, Group after, IMailingLists lists, IGroupwareGroups groupware, CancellationToken cancellationToken)
    {
        var failures = new List<Error>();
        if (before.Mail is { } mail && after.Mail is null)
        {
            Result ruled = await lists.SetSenderRuleAsync(mail.Address, null, cancellationToken).ConfigureAwait(false);
            Result<MailingList> list = await lists.FindByAddressAsync(mail.Address, cancellationToken).ConfigureAwait(false);
            Result destroyed = list.IsSuccess ? await lists.DestroyAsync(list.Value.Id, cancellationToken).ConfigureAwait(false) : list.Error;
            failures.AddRange(new[] { ruled.Error, destroyed.Error }.OfType<Error>().Where(static e => e.Kind != ErrorKind.NotFound));
        }

        if (before.Files is not null && after.Files is null)
        {
            Result deleted = await groupware.DeleteAsync(before.Key.GroupwareId(entity.Alias), cancellationToken).ConfigureAwait(false);
            if (deleted.Error is { Kind: not ErrorKind.NotFound } failed)
            {
                failures.Add(failed);
            }
        }

        return failures.Count switch
        {
            0 => Result.Success(),
            1 => failures[0],
            _ => failures[0] with { Message = string.Join(" ", failures.Select(static f => f.Message)) },
        };
    }
}
