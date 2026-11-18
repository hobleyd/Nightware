using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration;

/// <summary>
/// The console's authorization policy (ADR 0002 D6, D10). Pure; every use case calls it before touching a
/// port, so a forged form post gets the same answer as the UI would have shown.
/// </summary>
public static class AdminAuthorization
{
    public static Result RequireAdmin(AdminActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.IsAdmin ? Result.Success() : Error.Forbidden("You are not an administrator.");
    }

    public static Result RequireGlobal(AdminActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.IsGlobal ? Result.Success() : Error.Forbidden("Only a global administrator can do this.");
    }

    /// <summary>Global admins manage every entity; entity admins only their own.</summary>
    public static Result RequireEntity(AdminActor actor, string? entityId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.MayManage(entityId) ? Result.Success() : Error.Forbidden("You cannot manage this entity.");
    }

    /// <summary>
    /// Whether the actor may act on an existing user. Nobody may act on a service account, or on a Keycloak
    /// administrator, through the console, with one exception: where global admins administer the servers
    /// (<paramref name="globalAdminsAdministerServers"/>), a global admin may act on another global admin, whose
    /// Keycloak rights are the ones the actor holds already. Global admins may act on anyone else. An entity
    /// admin may act only on a user whose every entity is one the actor administers, and never on a global admin
    /// or an admin of an entity outside the actor's scope: otherwise resetting that user's password would hand
    /// the entity admin a wider scope than their own.
    /// </summary>
    public static Result RequireMayManageUser(AdminActor actor, IReadOnlyList<string> targetEntityIds, AdminScope targetScope, bool globalAdminsAdministerServers = false)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(targetEntityIds);
        ArgumentNullException.ThrowIfNull(targetScope);
        if (targetScope.IsServiceAccount
            || (targetScope.AdministersDirectory && !(globalAdminsAdministerServers && actor.IsGlobal && targetScope.IsGlobal)))
        {
            return Error.Forbidden("This account administers Keycloak; manage it in Keycloak itself.");
        }

        if (actor.IsGlobal)
        {
            return Result.Success();
        }

        if (targetEntityIds.Count == 0 || !targetEntityIds.All(actor.EntityIds.Contains))
        {
            return Error.Forbidden("You cannot manage this user.");
        }

        if (targetScope.IsGlobal || !targetScope.EntityIds.All(actor.EntityIds.Contains))
        {
            return Error.Forbidden("This user is an administrator outside your scope.");
        }

        return Result.Success();
    }

    /// <summary>Every address assigned to a user (primary and aliases) must be on one of their entity's domains.</summary>
    public static Result RequireAddressInEntity(Entity entity, MailAddress address)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(address);
        return entity.Owns(address)
            ? Result.Success()
            : Error.Validation($"{address.Domain} is not a domain of {entity.Name}.", "address");
    }

    /// <summary>The address is not one of the reserved system addresses.</summary>
    public static Result RequireNotReserved(MailAddress address, AdministrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(options);
        return options.ReservedLocalParts.Contains(address.LocalPart)
            ? Error.Validation($"{address} is a reserved system address.", "address")
            : Result.Success();
    }

    public static Result RequireNotSelf(AdminActor actor, string targetUserId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return string.Equals(actor.UserId, targetUserId, StringComparison.Ordinal)
            ? Error.Forbidden("You cannot do this to your own account.")
            : Result.Success();
    }

    public static Result RequireDisabled(IdentityAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Enabled ? Error.Validation("Offboard the user first; data can only be deleted for a disabled user.") : Result.Success();
    }

    /// <summary>Global admins, or entity admins where the deployment opted in (ADR 0002 OQ2b).</summary>
    public static Result RequireMayDeleteData(AdminActor actor, string? entityId, AdministrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(options);
        if (actor.IsGlobal || (options.EntityAdminsMayDeleteData && actor.MayManage(entityId)))
        {
            return Result.Success();
        }

        return Error.Forbidden("Only a global administrator can delete a user's data.");
    }

    /// <summary>
    /// The actor proved who they are recently and strongly enough. The host checks the same thing to
    /// send the admin back to Keycloak; this is the check a forged POST cannot skip.
    /// </summary>
    public static Result RequireFreshAuthentication(AdminActor actor, DateTimeOffset now, AdministrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(options);
        if (now - actor.AuthenticatedAt > options.DestructiveActionMaxAge)
        {
            return new Error(ErrorKind.Unauthorized, "reauthenticationRequired", "Sign in again to confirm this action.");
        }

        if (options.AcceptedAcrValues.Count > 0 && !options.AcceptedAcrValues.Contains(actor.Acr ?? string.Empty, StringComparer.Ordinal))
        {
            return new Error(ErrorKind.Unauthorized, "reauthenticationRequired", "This action needs multi-factor sign-in.");
        }

        return Result.Success();
    }

    /// <summary>The admin retyped the user's address exactly (case-insensitive, trimmed).</summary>
    public static Result RequireTypedConfirmation(string? typed, string expectedAddress) =>
        string.Equals(typed?.Trim(), expectedAddress, StringComparison.OrdinalIgnoreCase)
            ? Result.Success()
            : Error.Validation("The address you typed does not match the user's address.", "confirmation");
}
