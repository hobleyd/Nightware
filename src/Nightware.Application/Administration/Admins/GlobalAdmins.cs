using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Admins;

/// <summary>Everyone holding the global administrator role.</summary>
public sealed class ListGlobalAdmins(IAdminActor actor, IIdentityAccounts identities)
{
    public async Task<Result<IReadOnlyList<IdentityAccount>>> ExecuteAsync(CancellationToken cancellationToken)
    {
        Result allowed = AdminAuthorization.RequireGlobal(actor.Current);
        return allowed.IsSuccess ? await identities.ListGlobalAdminsAsync(cancellationToken).ConfigureAwait(false) : allowed.Error;
    }
}

/// <summary>
/// Grants or revokes the global administrator role. Global admins only, never on themselves; a new
/// global admin must set up a second factor before they can use the console. Granting the widest role there
/// is needs the same fresh, strong sign-in as deleting data; revoking narrows access and does not. Where
/// global admins administer the servers (<see cref="AdministrationOptions.GlobalAdminsAdministerServers"/>),
/// their mailbox's administrator role and their Nextcloud <c>admin</c> group membership follow (Keycloak's
/// realm-admin follows by itself, as part of the role).
/// </summary>
public sealed class SetGlobalAdmin(IAdminActor actor, IIdentityAccounts identities, IMailAccounts mail, IGroupwareAccounts groupware, IAuditLog audit, AdministrationOptions options, TimeProvider time)
{
    public async Task<Result> ExecuteAsync(string userId, bool granted, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result allowed = AdminAuthorization.RequireGlobal(actor.Current);
        if (allowed.IsSuccess)
        {
            allowed = AdminAuthorization.RequireNotSelf(actor.Current, userId);
        }

        if (allowed.IsSuccess && granted)
        {
            allowed = AdminAuthorization.RequireFreshAuthentication(actor.Current, time.GetUtcNow(), options);
        }

        if (!allowed.IsSuccess)
        {
            return allowed;
        }

        Result<IdentityAccount> target = await identities.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        if (granted && !target.Value.Enabled)
        {
            return Error.Conflict($"{target.Value.DisplayName} is offboarded; reinstate them before granting global administrator rights.");
        }

        Result result = await identities.SetGlobalAdminAsync(userId, granted, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && granted)
        {
            result = await identities.AddRequiredActionAsync(userId, RequiredAction.ConfigureTotp, cancellationToken).ConfigureAwait(false);
        }

        // After Keycloak, which decides who uses the console: a revoke must not wait on the other servers, and
        // one server refusing does not stop the other.
        if (result.IsSuccess && options.GlobalAdminsAdministerServers)
        {
            string?[] left =
            [
                await SetMailAdministratorAsync(target.Value, granted, cancellationToken).ConfigureAwait(false),
                await SetGroupwareAdministratorAsync(target.Value, granted, cancellationToken).ConfigureAwait(false),
            ];
            if (left.OfType<string>().ToList() is { Count: > 0 } failures)
            {
                result = Error.Conflict(granted
                    ? $"{target.Value.DisplayName} is a global administrator, but not yet an administrator of every server: {string.Join("; ", failures)}."
                    : $"{target.Value.DisplayName} is no longer a global administrator, but still administers a server: {string.Join("; ", failures)}. Take the role away there.");
            }
        }

        audit.Record(new AuditEvent(granted ? "global.admin.granted" : "global.admin.revoked", actor.Current.UserId, result.IsSuccess) { TargetUserId = userId });
        return result;
    }

    /// <summary>Null when the mailbox now matches (or there is none); otherwise what is left, in words.</summary>
    private async Task<string?> SetMailAdministratorAsync(IdentityAccount target, bool administrator, CancellationToken cancellationToken)
    {
        Result<MailAccount> mailbox = await mail.FindByAddressAsync(target.Email, cancellationToken).ConfigureAwait(false);
        if (!mailbox.IsSuccess)
        {
            return mailbox.Error.Kind == ErrorKind.NotFound ? null : $"the mail server: {mailbox.Error.Message}";
        }

        MailAccount account = mailbox.Value;
        if (!account.IsIndividual || !string.Equals(account.Address, target.Email, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (account.IsPrivileged && !account.IsAdministrator)
        {
            return $"the mailbox {account.Address} holds other mail-server rights, which the console leaves alone";
        }

        if (account.IsAdministrator == administrator)
        {
            return null;
        }

        Result set = await mail.SetAdministratorAsync(account.Id, administrator, cancellationToken).ConfigureAwait(false);
        return set.IsSuccess ? null : $"the mail server: {set.Error.Message}";
    }

    /// <summary>Null when the Nextcloud account now matches (or there is none); otherwise what is left, in words.</summary>
    private async Task<string?> SetGroupwareAdministratorAsync(IdentityAccount target, bool administrator, CancellationToken cancellationToken)
    {
        Result<GroupwareAccount> found = await groupware.FindAsync(target.Username, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error.Kind == ErrorKind.NotFound ? null : $"Nextcloud: {found.Error.Message}";
        }

        if (found.Value.IsAdminGroupMember == administrator)
        {
            return null;
        }

        // A local account of the same name is not theirs: Keycloak sign-in never reaches it.
        if (!found.Value.ManagedByIdentityProvider)
        {
            return $"the Nextcloud account {target.Username} is a local account, which the console leaves alone";
        }

        Result set = await groupware.SetAdministratorAsync(target.Username, administrator, cancellationToken).ConfigureAwait(false);
        return set.IsSuccess ? null : $"Nextcloud: {set.Error.Message}";
    }
}
