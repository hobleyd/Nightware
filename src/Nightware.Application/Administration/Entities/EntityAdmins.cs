using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>The administrators of one entity.</summary>
public sealed class ListEntityAdmins(IAdminActor actor, IEntityRegistry entities)
{
    public async Task<Result<IReadOnlyList<IdentityAccount>>> ExecuteAsync(string entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result allowed = AdminAuthorization.RequireEntity(actor.Current, entityId);
        return allowed.IsSuccess ? await entities.ListAdminsAsync(entityId, cancellationToken).ConfigureAwait(false) : allowed.Error;
    }
}

/// <summary>
/// Makes a member of an entity one of its administrators (ADR 0002 OQ5: entity admins may do this for
/// their own entity). Never the actor themselves; the new admin must set up a second factor.
/// </summary>
public sealed class GrantEntityAdmin(IAdminActor actor, IEntityRegistry entities, IIdentityAccounts identities, UserAccess users, IAuditLog audit)
{
    public async Task<Result> ExecuteAsync(string entityId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result allowed = AdminAuthorization.RequireEntity(actor.Current, entityId);
        if (allowed.IsSuccess)
        {
            allowed = AdminAuthorization.RequireNotSelf(actor.Current, userId);
        }

        if (!allowed.IsSuccess)
        {
            return allowed;
        }

        Result<ManagedUser> target = await users.LoadAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        Result<IReadOnlyList<string>> memberships = await entities.GetMembershipsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!memberships.IsSuccess)
        {
            return memberships.Error;
        }

        if (!memberships.Value.Contains(entityId, StringComparer.Ordinal))
        {
            return Error.Validation("Only a member of the entity can be one of its administrators.", "userId");
        }

        Result granted = await entities.GrantAdminAsync(entityId, userId, cancellationToken).ConfigureAwait(false);
        if (granted.IsSuccess)
        {
            granted = await identities.AddRequiredActionAsync(userId, RequiredAction.ConfigureTotp, cancellationToken).ConfigureAwait(false);
        }

        audit.Record(new AuditEvent("entity.admin.granted", actor.Current.UserId, granted.IsSuccess) { EntityId = entityId, TargetUserId = userId });
        return granted;
    }
}

/// <summary>Removes an entity administrator. Never the actor themselves (no accidental self-lockout).</summary>
public sealed class RevokeEntityAdmin(IAdminActor actor, IEntityRegistry entities, UserAccess users, IAuditLog audit)
{
    public async Task<Result> ExecuteAsync(string entityId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result allowed = AdminAuthorization.RequireEntity(actor.Current, entityId);
        if (allowed.IsSuccess)
        {
            allowed = AdminAuthorization.RequireNotSelf(actor.Current, userId);
        }

        if (!allowed.IsSuccess)
        {
            return allowed;
        }

        Result<ManagedUser> target = await users.LoadAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        Result revoked = await entities.RevokeAdminAsync(entityId, userId, cancellationToken).ConfigureAwait(false);
        audit.Record(new AuditEvent("entity.admin.revoked", actor.Current.UserId, revoked.IsSuccess) { EntityId = entityId, TargetUserId = userId });
        return revoked;
    }
}
