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
/// is needs the same fresh, strong sign-in as deleting data; revoking narrows access and does not.
/// </summary>
public sealed class SetGlobalAdmin(IAdminActor actor, IIdentityAccounts identities, IAuditLog audit, AdministrationOptions options, TimeProvider time)
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

        Result result = await identities.SetGlobalAdminAsync(userId, granted, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && granted)
        {
            result = await identities.AddRequiredActionAsync(userId, RequiredAction.ConfigureTotp, cancellationToken).ConfigureAwait(false);
        }

        audit.Record(new AuditEvent(granted ? "global.admin.granted" : "global.admin.revoked", actor.Current.UserId, result.IsSuccess) { TargetUserId = userId });
        return result;
    }
}
