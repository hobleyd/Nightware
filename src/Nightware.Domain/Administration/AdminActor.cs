namespace Nightware.Domain.Administration;

/// <summary>
/// The signed-in administrator, with a scope re-resolved from Keycloak (not trusted from the token).
/// </summary>
/// <param name="EntityIds">Entities whose <c>admins</c> group the actor belongs to.</param>
/// <param name="AuthenticatedAt">The ID token's <c>auth_time</c>: when the person last proved who they are.</param>
/// <param name="Acr">The ID token's <c>acr</c> (authentication strength), when present.</param>
public sealed record AdminActor(
    string UserId,
    bool IsGlobal,
    IReadOnlySet<string> EntityIds,
    DateTimeOffset AuthenticatedAt,
    string? Acr)
{
    public bool IsAdmin => IsGlobal || EntityIds.Count > 0;

    public bool MayManage(string? entityId) => IsGlobal || (entityId is not null && EntityIds.Contains(entityId));
}

/// <summary>An administrator's scope as the system of record holds it.</summary>
/// <param name="AdministersDirectory">
/// Holds Keycloak administration rights (or is a service account). The console never manages such an
/// account: resetting its password would hand over more than the console itself may do.
/// </param>
/// <param name="AccountEnabled">False for an offboarded user: they hold no console rights, whatever their roles.</param>
/// <param name="IsServiceAccount">A client's service account, never a person: off-limits whatever its roles.</param>
public sealed record AdminScope(bool IsGlobal, IReadOnlySet<string> EntityIds, bool AdministersDirectory = false, bool AccountEnabled = true, bool IsServiceAccount = false);
