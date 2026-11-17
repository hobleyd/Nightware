using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Users;

/// <summary>
/// Offboards a user (ADR 0002 D10): disables sign-in, ends their sessions, blocks every sign-in to their mailbox
/// (app passwords included) and disables their Nextcloud account. Deletes nothing: mail keeps arriving and is
/// kept, as are their files, calendars and contacts. Every step runs even when an earlier one fails, and a step
/// the console could not take or could not check is a failure, never a skip: the admin must be able to trust
/// that a successful offboarding shut every way in. Running it again on a disabled user finishes the job.
/// </summary>
public sealed class OffboardUser(IAdminActor actor, UserAccess users, IIdentityAccounts identities, IMailAccounts mail, IGroupwareAccounts groupware, IAuditLog audit)
{
    public async Task<Result<UserDetail>> ExecuteAsync(string userId, CancellationToken cancellationToken)
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

        var failures = new List<string>();
        Result result = await identities.SetEnabledAsync(userId, false, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            result = await identities.EndSessionsAsync(userId, cancellationToken).ConfigureAwait(false);
        }

        if (!result.IsSuccess)
        {
            failures.Add($"Keycloak: {result.Error.Message}");
        }

        // Mail sign-in too: app passwords and API keys the user made in Stalwart outlive their Keycloak account.
        BackendLink mailbox = loaded.Value.For(BackendKind.Mail);
        if (await ShutAsync("Mail", mailbox, () => mail.SetEnabledAsync(mailbox.DownstreamId!, false, cancellationToken)).ConfigureAwait(false) is { } mailFailure)
        {
            failures.Add(mailFailure);
        }

        string username = loaded.Value.User.Identity.Username;
        if (await ShutAsync("Nextcloud", loaded.Value.For(BackendKind.Groupware), () => groupware.SetEnabledAsync(username, false, cancellationToken)).ConfigureAwait(false) is { } cloudFailure)
        {
            failures.Add(cloudFailure);
        }

        audit.Record(new AuditEvent("user.offboarded", actor.Current.UserId, failures.Count == 0) { TargetUserId = userId, EntityId = loaded.Value.User.EntityId });
        return failures.Count == 0
            ? await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false)
            : Error.BackendUnavailable($"Offboarding is incomplete. {string.Join(" ", failures)} Use Finish offboarding once it is reachable.");
    }

    /// <summary>
    /// Disabled in Keycloak, but a mailbox or Nextcloud account may still let them sign in (or could not be
    /// checked): offboarding has to be run again.
    /// </summary>
    public static bool IsIncomplete(UserDetail user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return !user.User.Identity.Enabled && StillOpen(user).Any();
    }

    /// <summary>The downstream accounts that may still let a disabled user sign in.</summary>
    public static IEnumerable<BackendLink> StillOpen(UserDetail user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Links.Where(l => l.Backend != BackendKind.Identity && l.State is LinkState.Linked or LinkState.Error);
    }

    /// <summary>
    /// Disables a linked account. Disabled, missing and pending accounts have nothing to shut; a conflicting
    /// one is not this user's and is never touched; one that could not be checked is a failure.
    /// </summary>
    private static async Task<string?> ShutAsync(string name, BackendLink link, Func<Task<Result>> disable)
    {
        switch (link.State)
        {
            case LinkState.Linked:
                Result result = await disable().ConfigureAwait(false);
                return result.IsSuccess ? null : $"{name}: {result.Error.Message}";
            case LinkState.Error:
                return $"{name} could not be checked ({link.Detail}), so signing in there is not blocked yet.";
            default:
                return null;
        }
    }
}

/// <summary>
/// Reverses an offboarding: enables sign-in and the Nextcloud account. A mailbox the console deleted is
/// recreated (new and empty) only when the admin asked for it; Nextcloud data that was deleted comes back
/// as a new, empty Nextcloud user. Reopening access is the security-critical direction, so it needs the same
/// fresh, strong sign-in as granting global administrator rights.
/// </summary>
public sealed class ReinstateUser(IAdminActor actor, UserAccess users, IIdentityAccounts identities, IMailAccounts mail, IGroupwareAccounts groupware, IAuditLog audit, AdministrationOptions options, TimeProvider time)
{
    public async Task<Result<UserDetail>> ExecuteAsync(string userId, bool recreateMailbox, CancellationToken cancellationToken)
    {
        Result self = AdminAuthorization.RequireNotSelf(actor.Current, userId);
        if (self.IsSuccess)
        {
            self = AdminAuthorization.RequireFreshAuthentication(actor.Current, time.GetUtcNow(), options);
        }

        if (!self.IsSuccess)
        {
            return self.Error;
        }

        Result<UserDetail> loaded = await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        IdentityAccount identity = loaded.Value.User.Identity;
        Result result = await identities.SetEnabledAsync(userId, true, cancellationToken).ConfigureAwait(false);

        BackendLink cloud = loaded.Value.For(BackendKind.Groupware);
        if (result.IsSuccess && cloud.State == LinkState.Disabled)
        {
            result = await groupware.SetEnabledAsync(identity.Username, true, cancellationToken).ConfigureAwait(false);
        }
        else if (result.IsSuccess && cloud.State == LinkState.Missing)
        {
            Result created = await groupware.PreProvisionAsync(identity.Username, identity.Email, identity.DisplayName, cancellationToken).ConfigureAwait(false);
            result = created.IsSuccess || created.Error.Kind == ErrorKind.Unsupported ? Result.Success() : created;
            if (result.IsSuccess && identity.GroupwareDeletedAt is not null)
            {
                result = await identities.SetDeletionMarkerAsync(userId, DeletionTarget.GroupwareData, null, cancellationToken).ConfigureAwait(false);
            }
        }

        BackendLink mailLink = loaded.Value.For(BackendKind.Mail);
        if (result.IsSuccess && mailLink.State == LinkState.Disabled)
        {
            result = await mail.SetEnabledAsync(mailLink.DownstreamId!, true, cancellationToken).ConfigureAwait(false);
        }

        bool recreated = false;
        if (result.IsSuccess && recreateMailbox && loaded.Value.For(BackendKind.Mail).State == LinkState.Missing)
        {
            if (!MailAddress.TryParse(identity.Email, out MailAddress? address))
            {
                return Error.Validation("The user's address cannot be used for a mailbox.");
            }

            Result<MailAccount> created = await mail.CreateAsync(address, identity.Username, cancellationToken).ConfigureAwait(false);
            result = created.IsSuccess ? await identities.SetDeletionMarkerAsync(userId, DeletionTarget.Mail, null, cancellationToken).ConfigureAwait(false) : created.Error;
            recreated = created.IsSuccess;
        }

        audit.Record(new AuditEvent("user.reinstated", actor.Current.UserId, result.IsSuccess)
        {
            TargetUserId = userId,
            EntityId = loaded.Value.User.EntityId,
            Details = new Dictionary<string, string?> { ["mailboxRecreated"] = recreated ? "true" : "false" },
        });
        return result.IsSuccess ? await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false) : result.Error;
    }
}
