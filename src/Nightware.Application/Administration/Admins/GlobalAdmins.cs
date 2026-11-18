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
/// their mailbox's administrator role follows (Keycloak's realm-admin follows by itself, as part of the role).
/// </summary>
public sealed class SetGlobalAdmin(IAdminActor actor, IIdentityAccounts identities, IMailAccounts mail, IAuditLog audit, AdministrationOptions options, TimeProvider time)
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

        // After Keycloak, which decides who uses the console: a revoke must not wait on the mail server.
        if (result.IsSuccess && options.GlobalAdminsAdministerServers)
        {
            result = await SetMailAdministratorAsync(target.Value, granted, cancellationToken).ConfigureAwait(false);
        }

        audit.Record(new AuditEvent(granted ? "global.admin.granted" : "global.admin.revoked", actor.Current.UserId, result.IsSuccess) { TargetUserId = userId });
        return result;
    }

    private async Task<Result> SetMailAdministratorAsync(IdentityAccount target, bool administrator, CancellationToken cancellationToken)
    {
        Result<MailAccount> mailbox = await mail.FindByAddressAsync(target.Email, cancellationToken).ConfigureAwait(false);
        if (!mailbox.IsSuccess)
        {
            return mailbox.Error.Kind == ErrorKind.NotFound ? Result.Success() : Partly(target, administrator, mailbox.Error.Message);
        }

        MailAccount account = mailbox.Value;
        if (!account.IsIndividual || !string.Equals(account.Address, target.Email, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Success();
        }

        if (account.IsPrivileged && !account.IsAdministrator)
        {
            return Partly(target, administrator, $"{account.Address} holds other mail-server rights, which the console leaves alone");
        }

        if (account.IsAdministrator == administrator)
        {
            return Result.Success();
        }

        Result set = await mail.SetAdministratorAsync(account.Id, administrator, cancellationToken).ConfigureAwait(false);
        return set.IsSuccess ? set : Partly(target, administrator, set.Error.Message);
    }

    private static Result Partly(IdentityAccount target, bool administrator, string why) =>
        Error.Conflict(administrator
            ? $"{target.DisplayName} is a global administrator, but their mailbox is not a mail-server administrator: {why}."
            : $"{target.DisplayName} is no longer a global administrator, but their mailbox is still a mail-server administrator: {why}. Take the role away in the mail server.");
}
