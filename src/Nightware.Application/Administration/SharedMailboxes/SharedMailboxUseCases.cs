using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.SharedMailboxes;

/// <summary>An entity's shared mailboxes, by name.</summary>
public sealed class ListSharedMailboxes(SharedMailboxAccess access, IEntityMailboxes mailboxes)
{
    public async Task<Result<(Entity Entity, IReadOnlyList<SharedMailbox> Mailboxes)>> ExecuteAsync(string entityId, CancellationToken cancellationToken)
    {
        Result<Entity> entity = await access.LoadEntityAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess)
        {
            return entity.Error;
        }

        Result<IReadOnlyList<SharedMailbox>> listed = await mailboxes.ListAsync(entityId, cancellationToken).ConfigureAwait(false);
        return listed.IsSuccess
            ? (entity.Value, [.. listed.Value.OrderBy(static m => m.Name, StringComparer.CurrentCultureIgnoreCase)])
            : listed.Error;
    }
}

/// <summary>One shared mailbox with the people who may open it and its state on the mail server.</summary>
public sealed class GetSharedMailbox(SharedMailboxAccess access)
{
    public Task<Result<SharedMailboxDetail>> ExecuteAsync(string entityId, string mailboxId, CancellationToken cancellationToken) =>
        access.LoadDetailAsync(entityId, mailboxId, cancellationToken);
}

/// <summary>The shared mailboxes a user may open, in the entities the actor manages.</summary>
public sealed class ListUserSharedMailboxes(IAdminActor actor, UserAccess users, IEntityRegistry entities, IEntityMailboxes mailboxes)
{
    public async Task<Result<IReadOnlyList<SharedMailbox>>> ExecuteAsync(string userId, CancellationToken cancellationToken)
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

        var found = new List<SharedMailbox>();
        foreach (string entityId in memberships.Value.Where(actor.Current.MayManage))
        {
            Result<IReadOnlyList<SharedMailbox>> mine = await mailboxes.ListUserMailboxesAsync(entityId, userId, cancellationToken).ConfigureAwait(false);
            if (!mine.IsSuccess)
            {
                return mine.Error;
            }

            found.AddRange(mine.Value);
        }

        return found.OrderBy(static m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

/// <summary>
/// Creates a shared mailbox in the record, then on the mail server. A mail server that fails leaves the record
/// with the mailbox Missing, for Repair.
/// </summary>
public sealed class CreateSharedMailbox(IAdminActor actor, SharedMailboxAccess access, IEntityMailboxes mailboxes, SharedMailboxSync sync, IAuditLog audit)
{
    public async Task<Result<SharedMailboxSyncOutcome>> ExecuteAsync(string entityId, SharedMailboxForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        Result<Entity> entity = await access.LoadEntityAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess)
        {
            return entity.Error;
        }

        Result<(string Name, MailAddress Address, IReadOnlyList<MailAddress> Aliases)> read = await access.ReadAsync(entity.Value, form, null, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        Result<SharedMailbox> created = await mailboxes.CreateAsync(entityId, new NewSharedMailbox(read.Value.Address, read.Value.Name) { Aliases = read.Value.Aliases }, cancellationToken).ConfigureAwait(false);
        Result<SharedMailboxSyncOutcome> outcome = created.IsSuccess
            ? await sync.ApplyAsync(created.Value, [], cancellationToken).ConfigureAwait(false)
            : created.Error;
        audit.Record(new AuditEvent("mailbox.created", actor.Current.UserId, outcome.IsSuccess)
        {
            EntityId = entityId,
            Details = Details(created.IsSuccess ? created.Value.Id : null, read.Value.Address),
        });
        return outcome;
    }

    internal static Dictionary<string, string?> Details(string? mailboxId, MailAddress address) => new()
    {
        ["mailboxId"] = mailboxId,
        ["address"] = address.Value,
    };
}

/// <summary>Changes a shared mailbox's name and aliases, then brings the mail server into line.</summary>
public sealed class UpdateSharedMailbox(IAdminActor actor, SharedMailboxAccess access, IEntityMailboxes mailboxes, SharedMailboxSync sync, IAuditLog audit)
{
    public async Task<Result<SharedMailboxSyncOutcome>> ExecuteAsync(string entityId, string mailboxId, SharedMailboxForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        Result<(Entity Entity, SharedMailbox Mailbox)> loaded = await access.LoadAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        (Entity entity, SharedMailbox before) = loaded.Value;
        Result<(string Name, MailAddress Address, IReadOnlyList<MailAddress> Aliases)> read = await access.ReadAsync(entity, form, before, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        Result<SharedMailbox> updated = await mailboxes.UpdateAsync(before with { Name = read.Value.Name, Aliases = read.Value.Aliases }, cancellationToken).ConfigureAwait(false);
        Result<SharedMailboxSyncOutcome> outcome = updated.IsSuccess
            ? await SharedMailboxMembership.SyncAsync(mailboxes, sync, updated.Value, cancellationToken).ConfigureAwait(false)
            : updated.Error;
        audit.Record(new AuditEvent("mailbox.updated", actor.Current.UserId, outcome.IsSuccess)
        {
            EntityId = entityId,
            Details = CreateSharedMailbox.Details(mailboxId, before.Address),
        });
        return outcome;
    }
}

/// <summary>Lets a user of the entity open one of its shared mailboxes.</summary>
public sealed class AddSharedMailboxMember(IAdminActor actor, SharedMailboxAccess access, IEntityRegistry entities, IEntityMailboxes mailboxes, SharedMailboxSync sync, IAuditLog audit)
{
    public async Task<Result<SharedMailboxSyncOutcome>> ExecuteAsync(string entityId, string mailboxId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result<(Entity Entity, SharedMailbox Mailbox)> loaded = await access.LoadAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
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
            return Error.Validation($"Only a user of {loaded.Value.Entity.Name} can open its shared mailboxes.", "userId");
        }

        Result added = await mailboxes.AddMemberAsync(entityId, mailboxId, userId, cancellationToken).ConfigureAwait(false);
        Result<SharedMailboxSyncOutcome> outcome = added.IsSuccess
            ? await SharedMailboxMembership.SyncAsync(mailboxes, sync, loaded.Value.Mailbox, cancellationToken).ConfigureAwait(false)
            : added.Error;
        audit.Record(new AuditEvent("mailbox.member.added", actor.Current.UserId, outcome.IsSuccess)
        {
            EntityId = entityId,
            TargetUserId = userId,
            Details = new Dictionary<string, string?> { ["mailboxId"] = mailboxId },
        });
        return outcome;
    }
}

/// <summary>Takes a user's access to a shared mailbox away, at once.</summary>
public sealed class RemoveSharedMailboxMember(IAdminActor actor, SharedMailboxAccess access, IEntityMailboxes mailboxes, SharedMailboxSync sync, IAuditLog audit)
{
    public async Task<Result<SharedMailboxSyncOutcome>> ExecuteAsync(string entityId, string mailboxId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result<(Entity Entity, SharedMailbox Mailbox)> loaded = await access.LoadAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        Result removed = await mailboxes.RemoveMemberAsync(entityId, mailboxId, userId, cancellationToken).ConfigureAwait(false);
        Result<SharedMailboxSyncOutcome> outcome = removed.IsSuccess
            ? await SharedMailboxMembership.SyncAsync(mailboxes, sync, loaded.Value.Mailbox, cancellationToken).ConfigureAwait(false)
            : removed.Error;
        audit.Record(new AuditEvent("mailbox.member.removed", actor.Current.UserId, outcome.IsSuccess)
        {
            EntityId = entityId,
            TargetUserId = userId,
            Details = new Dictionary<string, string?> { ["mailboxId"] = mailboxId },
        });
        return outcome;
    }
}

/// <summary>Builds a missing shared mailbox and brings its members back into line with the record.</summary>
public sealed class RepairSharedMailbox(IAdminActor actor, SharedMailboxAccess access, IEntityMailboxes mailboxes, SharedMailboxSync sync, IAuditLog audit)
{
    public async Task<Result<SharedMailboxSyncOutcome>> ExecuteAsync(string entityId, string mailboxId, CancellationToken cancellationToken)
    {
        Result<(Entity Entity, SharedMailbox Mailbox)> loaded = await access.LoadAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        Result<SharedMailboxSyncOutcome> outcome = await SharedMailboxMembership.SyncAsync(mailboxes, sync, loaded.Value.Mailbox, cancellationToken).ConfigureAwait(false);
        audit.Record(new AuditEvent("mailbox.repaired", actor.Current.UserId, outcome.IsSuccess)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?> { ["mailboxId"] = mailboxId },
        });
        return outcome;
    }
}

/// <summary>
/// Deletes a shared mailbox and every message in it: its members are taken out and its mail server account is
/// destroyed, then the record goes, last, so a failure leaves it listed where deleting can be tried again. Global
/// admins only (or entity admins where the deployment lets them delete data), behind a fresh sign-in and the
/// mailbox's address typed again, like deleting a user's mail.
/// </summary>
public sealed class DeleteSharedMailbox(
    IAdminActor actor,
    SharedMailboxAccess access,
    IEntityMailboxes mailboxes,
    ISharedMailAccounts shared,
    IMailAccounts mail,
    IAuditLog audit,
    AdministrationOptions options,
    TimeProvider time)
{
    public async Task<Result> ExecuteAsync(string entityId, string mailboxId, string? typedAddress, CancellationToken cancellationToken)
    {
        Result<(Entity Entity, SharedMailbox Mailbox)> loaded = await access.LoadAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        SharedMailbox mailbox = loaded.Value.Mailbox;
        Result allowed = Check(mailbox, typedAddress ?? string.Empty);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        Result<SharedMailAccount> found = await shared.FindByAddressAsync(mailbox.Address, cancellationToken).ConfigureAwait(false);
        Result destroyed = found.IsSuccess
            ? found.Value.IsPrivileged
                ? Error.Conflict($"The mail server's account at {mailbox.Address} carries a role or permissions of its own; the console never deletes it.", "address")
                : await shared.DestroyAsync(found.Value.Id, cancellationToken).ConfigureAwait(false)
            : found.Error;
        bool absent = destroyed.Error is { Kind: ErrorKind.NotFound };
        Result removed = destroyed.IsSuccess || absent
            ? await mailboxes.DeleteAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false) is { IsSuccess: false, Error.Kind: not ErrorKind.NotFound } failed
                ? failed
                : Result.Success()
            : destroyed;
        audit.Record(new AuditEvent("mailbox.deleted", actor.Current.UserId, removed.IsSuccess)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?>
            {
                ["mailboxId"] = mailboxId,
                ["address"] = mailbox.Address.Value,
                ["mail"] = absent ? "absent" : destroyed.IsSuccess ? "destroyed" : "failed",
            },
        });
        return removed;
    }

    /// <summary>Bytes the shared mailbox holds, for the confirmation page; null when it is not on the mail server.</summary>
    public async Task<Result<long?>> GetUsageAsync(SharedMailbox mailbox, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        Result<SharedMailAccount> found = await shared.FindByAddressAsync(mailbox.Address, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error.Kind == ErrorKind.NotFound ? Result<long?>.Success(null) : found.Error;
        }

        Result<long> used = await mail.GetUsageAsync(found.Value.Id, cancellationToken).ConfigureAwait(false);
        return used.IsSuccess ? used.Value : used.Error;
    }

    /// <summary>
    /// Whether the actor may delete the shared mailbox now; the page asks with a null <paramref name="typedAddress"/>
    /// before showing the confirmation.
    /// </summary>
    public Result Check(SharedMailbox mailbox, string? typedAddress)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        Result allowed = AdminAuthorization.RequireMayDeleteData(actor.Current, mailbox.EntityId, options);
        if (allowed.IsSuccess)
        {
            allowed = AdminAuthorization.RequireFreshAuthentication(actor.Current, time.GetUtcNow(), options);
        }

        if (allowed.IsSuccess && typedAddress is not null && !mailbox.Address.Matches(typedAddress))
        {
            allowed = Error.Validation("The address you typed does not match the shared mailbox's address.", "confirmation");
        }

        return allowed;
    }
}

internal static class SharedMailboxMembership
{
    public static async Task<Result<SharedMailboxSyncOutcome>> SyncAsync(IEntityMailboxes mailboxes, SharedMailboxSync sync, SharedMailbox mailbox, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<IdentityAccount>> members = await mailboxes.ListMembersAsync(mailbox.EntityId, mailbox.Id, cancellationToken).ConfigureAwait(false);
        return members.IsSuccess ? await sync.ApplyAsync(mailbox, members.Value, cancellationToken).ConfigureAwait(false) : members.Error;
    }
}
