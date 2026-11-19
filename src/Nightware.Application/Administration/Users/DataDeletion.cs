using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Users;

public sealed record DeletionOutcome(bool AlreadyAbsent, long? BytesDeleted);

/// <summary>
/// The guard rails every data deletion passes (ADR 0002 D10): permission to delete data, not the actor's own
/// account, a disabled target, a fresh and strong sign-in, the retyped address, and a downstream account
/// that provably belongs to this user. Checked in the use case, so a forged POST meets the same rules.
/// </summary>
public sealed class DeletionGuard(IAdminActor actor, UserAccess users, IEntityRegistry entities, AdministrationOptions options, TimeProvider time)
{
    public async Task<Result<UserDetail>> LoadAsync(string userId, CancellationToken cancellationToken)
    {
        Result self = AdminAuthorization.RequireNotSelf(actor.Current, userId);
        if (!self.IsSuccess)
        {
            return self.Error;
        }

        Result<UserDetail> loaded = await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        Result allowed = AdminAuthorization.RequireMayDeleteData(actor.Current, loaded.Value.User.EntityId, options);
        if (allowed.IsSuccess)
        {
            allowed = AdminAuthorization.RequireDisabled(loaded.Value.User.Identity);
        }

        // Deleting data while a mailbox or Nextcloud account may still let them in would leave the
        // offboarding half done and the evidence gone.
        if (allowed.IsSuccess && OffboardUser.IsIncomplete(loaded.Value))
        {
            // Names only what is actually open, so the refusal points at the system to wait for.
            string[] open = [.. OffboardUser.StillOpen(loaded.Value).Select(static l => l.Backend == BackendKind.Mail ? "their mailbox" : "their Files & calendar account")];
            allowed = Error.Validation($"Finish offboarding first: {string.Join(" and ", open)} may still let them sign in.");
        }

        return allowed.IsSuccess ? loaded : allowed.Error;
    }

    public Result Confirm(UserDetail user, string? typedAddress)
    {
        ArgumentNullException.ThrowIfNull(user);
        Result fresh = AdminAuthorization.RequireFreshAuthentication(actor.Current, time.GetUtcNow(), options);
        return fresh.IsSuccess ? AdminAuthorization.RequireTypedConfirmation(typedAddress, user.User.Identity.Email) : fresh;
    }

    /// <summary>
    /// Refuses while the user receives a domain's mail to unknown addresses: the mail server would go on accepting
    /// it for a mailbox that no longer exists and bounce every message afterwards.
    /// </summary>
    public async Task<Result> RequireNoCatchAllAsync(UserDetail user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.User.EntityId is not { } entityId)
        {
            return Result.Success();
        }

        Result<Entity> entity = await entities.GetAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess)
        {
            return entity.Error;
        }

        IReadOnlyList<MailDomain> domains = entity.Value.CatchAllsOf(user.User.Identity.Id);
        return domains.Count == 0
            ? Result.Success()
            : Error.Conflict($"{user.User.Identity.Email} receives the mail to unknown addresses on {string.Join(", ", domains.Select(static d => d.Name))}; choose someone else for it on the entity's page first.");
    }

    /// <summary>Who receives mail to <paramref name="address"/> once its mailbox is gone: the domain's catch-all user, or null when it bounces.</summary>
    public async Task<string?> CatchAllAfterAsync(UserDetail user, string address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.User.EntityId is not { } entityId || !MailAddress.TryParse(address, out MailAddress? parsed))
        {
            return null;
        }

        Result<Entity> entity = await entities.GetAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess || entity.Value.CatchAllFor(parsed.Domain) is not { } holderId)
        {
            return null;
        }

        Result<ManagedUser> holder = await users.LoadAsync(holderId, cancellationToken).ConfigureAwait(false);
        return holder.IsSuccess ? holder.Value.Identity.Email : "the domain's catch-all user";
    }

    public static Error? RefuseConflict(BackendLink link) =>
        link.State is LinkState.Conflict or LinkState.Error
            ? Error.Conflict($"Not deleted: {link.Detail} The console only deletes an account it can prove is this user's.")
            : null;
}

/// <summary>What deleting a user's mail or Nextcloud data would remove and keep, for the confirmation page.</summary>
public sealed class GetDeletionPreview(DeletionGuard guard, IMailAccounts mail)
{
    public async Task<Result<DeletionPreview>> ExecuteAsync(string userId, DeletionTarget target, CancellationToken cancellationToken)
    {
        Result<UserDetail> loaded = await guard.LoadAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        UserDetail user = loaded.Value;
        IdentityAccount identity = user.User.Identity;
        if (target == DeletionTarget.Mail)
        {
            BackendLink mailbox = user.For(BackendKind.Mail);
            long? bytes = null;
            if (mailbox.State is LinkState.Linked or LinkState.Disabled)
            {
                Result<long> usage = await mail.GetUsageAsync(mailbox.DownstreamId!, cancellationToken).ConfigureAwait(false);
                bytes = usage.IsSuccess ? usage.Value : null;
            }

            string afterwards = await guard.CatchAllAfterAsync(user, identity.Email, cancellationToken).ConfigureAwait(false) is { } catchAll
                ? $"Mail sent to {identity.Email} afterwards goes to {catchAll}, who receives the domain's mail to unknown addresses"
                : $"Mail sent to {identity.Email} afterwards bounces";
            return new DeletionPreview(
                user.User,
                target,
                mailbox.State == LinkState.Missing,
                bytes,
                [$"The mailbox {identity.Email} and every message in it", "Its aliases, identities, filters and vacation reply", afterwards],
                [$"Their sign-in account {identity.Username} (disabled)", "Their files, calendars, contacts and tasks"]);
        }

        BackendLink cloud = user.For(BackendKind.Groupware);
        return new DeletionPreview(
            user.User,
            target,
            cloud.State == LinkState.Missing,
            null,
            [$"Their Files & calendar account {identity.Username}", "Their files, calendars, contacts, tasks and shares"],
            [$"Their sign-in account {identity.Username} (disabled)", $"The mailbox {identity.Email} and its mail"]);
    }
}

/// <summary>Destroys a disabled user's mailbox and every message in it. Keycloak and Nextcloud are untouched.</summary>
public sealed class DeleteUserMail(IAdminActor actor, DeletionGuard guard, IMailAccounts mail, IIdentityAccounts identities, TimeProvider time, IAuditLog audit, AccessHistoryRecorder history)
{
    public async Task<Result<DeletionOutcome>> ExecuteAsync(string userId, string? typedAddress, CancellationToken cancellationToken)
    {
        Result<UserDetail> loaded = await guard.LoadAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        Result confirmed = guard.Confirm(loaded.Value, typedAddress);
        if (!confirmed.IsSuccess)
        {
            return confirmed.Error;
        }

        BackendLink mailbox = loaded.Value.For(BackendKind.Mail);
        if (DeletionGuard.RefuseConflict(mailbox) is { } conflict)
        {
            Record(loaded.Value, false, "refused:" + mailbox.State, null);
            return conflict;
        }

        Result catchAll = await guard.RequireNoCatchAllAsync(loaded.Value, cancellationToken).ConfigureAwait(false);
        if (!catchAll.IsSuccess)
        {
            Record(loaded.Value, false, "refused:catch-all", null);
            return catchAll.Error;
        }

        if (mailbox.State == LinkState.Missing)
        {
            Record(loaded.Value, true, "already absent", null);
            return new DeletionOutcome(AlreadyAbsent: true, BytesDeleted: null);
        }

        Result<long> usage = await mail.GetUsageAsync(mailbox.DownstreamId!, cancellationToken).ConfigureAwait(false);
        long? bytes = usage.IsSuccess ? usage.Value : null;
        Result destroyed = await mail.DestroyAsync(mailbox.DownstreamId!, cancellationToken).ConfigureAwait(false);
        bool absent = !destroyed.IsSuccess && destroyed.Error.Kind == ErrorKind.NotFound;
        if (!destroyed.IsSuccess && !absent)
        {
            Record(loaded.Value, false, "failed", bytes);
            return destroyed.Error;
        }

        Result marked = await identities.SetDeletionMarkerAsync(userId, DeletionTarget.Mail, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        Record(loaded.Value, true, absent ? "already absent" : marked.IsSuccess ? "deleted" : "deleted; marker not written", bytes);
        await history.RecordAsync(userId, AccessEventKind.MailDeleted, AccessHistoryRecorder.WithGone(loaded.Value, BackendKind.Mail), cancellationToken).ConfigureAwait(false);
        return new DeletionOutcome(absent, absent ? null : bytes);
    }

    private void Record(UserDetail user, bool succeeded, string outcome, long? bytes) =>
        audit.Record(new AuditEvent("user.mail.deleted", actor.Current.UserId, succeeded)
        {
            TargetUserId = user.User.Identity.Id,
            EntityId = user.User.EntityId,
            Details = new Dictionary<string, string?>
            {
                ["address"] = user.User.Identity.Email,
                ["mailbox"] = user.For(BackendKind.Mail).DownstreamId,
                ["bytes"] = bytes?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["outcome"] = outcome,
            },
        });
}

/// <summary>Deletes a disabled user's Nextcloud account and everything in it. Keycloak and the mailbox are untouched.</summary>
public sealed class DeleteUserGroupwareData(IAdminActor actor, DeletionGuard guard, IGroupwareAccounts groupware, IIdentityAccounts identities, TimeProvider time, IAuditLog audit, AccessHistoryRecorder history)
{
    public async Task<Result<DeletionOutcome>> ExecuteAsync(string userId, string? typedAddress, CancellationToken cancellationToken)
    {
        Result<UserDetail> loaded = await guard.LoadAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        Result confirmed = guard.Confirm(loaded.Value, typedAddress);
        if (!confirmed.IsSuccess)
        {
            return confirmed.Error;
        }

        BackendLink cloud = loaded.Value.For(BackendKind.Groupware);
        if (DeletionGuard.RefuseConflict(cloud) is { } conflict)
        {
            Record(loaded.Value, false, "refused:" + cloud.State);
            return conflict;
        }

        if (cloud.State == LinkState.Missing)
        {
            Record(loaded.Value, true, "already absent");
            return new DeletionOutcome(AlreadyAbsent: true, BytesDeleted: null);
        }

        Result deleted = await groupware.DeleteAsync(cloud.DownstreamId!, cancellationToken).ConfigureAwait(false);
        bool absent = !deleted.IsSuccess && deleted.Error.Kind == ErrorKind.NotFound;
        if (!deleted.IsSuccess && !absent)
        {
            Record(loaded.Value, false, "failed");
            return deleted.Error;
        }

        Result marked = await identities.SetDeletionMarkerAsync(userId, DeletionTarget.GroupwareData, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        Record(loaded.Value, true, absent ? "already absent" : marked.IsSuccess ? "deleted" : "deleted; marker not written");
        await history.RecordAsync(userId, AccessEventKind.FilesDeleted, AccessHistoryRecorder.WithGone(loaded.Value, BackendKind.Groupware), cancellationToken).ConfigureAwait(false);
        return new DeletionOutcome(absent, null);
    }

    private void Record(UserDetail user, bool succeeded, string outcome) =>
        audit.Record(new AuditEvent("user.groupware.deleted", actor.Current.UserId, succeeded)
        {
            TargetUserId = user.User.Identity.Id,
            EntityId = user.User.EntityId,
            Details = new Dictionary<string, string?> { ["uid"] = user.User.Identity.Username, ["outcome"] = outcome },
        });
}
