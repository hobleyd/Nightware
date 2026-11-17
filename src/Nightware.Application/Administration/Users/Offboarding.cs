using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Users;

/// <summary>
/// Offboards a user (ADR 0002 D10): disables sign-in, ends their sessions, blocks every sign-in to their mailbox
/// (app passwords included) and disables their Nextcloud account. Deletes nothing: mail keeps arriving and is
/// kept, as are their files, calendars and contacts.
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

        Result result = await identities.SetEnabledAsync(userId, false, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            result = await identities.EndSessionsAsync(userId, cancellationToken).ConfigureAwait(false);
        }

        // Mail sign-in too: app passwords and API keys the user made in Stalwart outlive their Keycloak account.
        BackendLink mailbox = loaded.Value.For(BackendKind.Mail);
        if (result.IsSuccess && mailbox.State == LinkState.Linked)
        {
            result = await mail.SetEnabledAsync(mailbox.DownstreamId!, false, cancellationToken).ConfigureAwait(false);
        }

        if (result.IsSuccess && loaded.Value.For(BackendKind.Groupware).State == LinkState.Linked)
        {
            result = await groupware.SetEnabledAsync(loaded.Value.User.Identity.Username, false, cancellationToken).ConfigureAwait(false);
        }

        audit.Record(new AuditEvent("user.offboarded", actor.Current.UserId, result.IsSuccess) { TargetUserId = userId, EntityId = loaded.Value.User.EntityId });
        return result.IsSuccess ? await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false) : result.Error;
    }
}

/// <summary>
/// Reverses an offboarding: enables sign-in and the Nextcloud account. A mailbox the console deleted is
/// recreated (new and empty) only when the admin asked for it; Nextcloud data that was deleted comes back
/// as a new, empty Nextcloud user.
/// </summary>
public sealed class ReinstateUser(IAdminActor actor, UserAccess users, IIdentityAccounts identities, IMailAccounts mail, IGroupwareAccounts groupware, IAuditLog audit)
{
    public async Task<Result<UserDetail>> ExecuteAsync(string userId, bool recreateMailbox, CancellationToken cancellationToken)
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
