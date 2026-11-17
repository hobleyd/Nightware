using Microsoft.Extensions.Logging;

using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Administration;

/// <summary>
/// The one place that knows how "entity admin" is represented in Keycloak (ADR 0002 D1): membership of the
/// top-level <b>organization group</b> <c>admins</c> of that organization (Keycloak 26.6+), a group that
/// carries the realm role <c>nightware-entity-admin</c> (role inheritance through organization groups,
/// 26.7+). Both <see cref="KeycloakEntityRegistry"/> and <see cref="KeycloakAdminScopeSource"/> go through
/// here, so changing the representation touches this class only.
/// </summary>
internal sealed partial class KeycloakEntityAdmins(IKeycloakAdminClient keycloak, ILogger<KeycloakEntityAdmins> logger)
{
    public const string GroupName = "admins";

    /// <summary>
    /// Whether <paramref name="userId"/> administers <paramref name="organizationId"/>: false when the organization
    /// has no <c>admins</c> group or the user is not a member of it.
    /// </summary>
    public async Task<Result<bool>> IsAdminAsync(string organizationId, string userId, CancellationToken cancellationToken)
    {
        Result<KeycloakGroup?> group = await FindAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (!group.IsSuccess)
        {
            return group.Error;
        }

        return group.Value is { } admins
            ? await IsInGroupAsync(organizationId, admins.Id, userId, cancellationToken).ConfigureAwait(false)
            : false;
    }

    /// <summary>The organization's admins; empty when it has no <c>admins</c> group yet.</summary>
    public async Task<Result<IReadOnlyList<KeycloakUser>>> ListAsync(string organizationId, CancellationToken cancellationToken)
    {
        Result<KeycloakGroup?> group = await FindAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (!group.IsSuccess)
        {
            return group.Error;
        }

        return group.Value is { } admins
            ? await keycloak.ListOrganizationGroupMembersAsync(organizationId, admins.Id, cancellationToken).ConfigureAwait(false)
            : Array.Empty<KeycloakUser>();
    }

    /// <summary>Idempotent; creates the <c>admins</c> group (with its realm role) on first use.</summary>
    public async Task<Result> GrantAsync(string organizationId, string userId, CancellationToken cancellationToken)
    {
        Result<string> group = await EnsureGroupAsync(organizationId, cancellationToken).ConfigureAwait(false);
        return group.IsSuccess
            ? await keycloak.AddOrganizationGroupMemberAsync(organizationId, group.Value, userId, cancellationToken).ConfigureAwait(false)
            : group.Error;
    }

    /// <summary>Idempotent; never creates the group. A user who is not a member of the organization is not an admin of it.</summary>
    public async Task<Result> RevokeAsync(string organizationId, string userId, CancellationToken cancellationToken)
    {
        Result<KeycloakGroup?> group = await FindAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (!group.IsSuccess)
        {
            return group.Error;
        }

        if (group.Value is not { } admins)
        {
            return Result.Success();
        }

        // Keycloak answers 400 to removing a non-member, so look first.
        Result<bool> isAdmin = await IsInGroupAsync(organizationId, admins.Id, userId, cancellationToken).ConfigureAwait(false);
        if (!isAdmin.IsSuccess)
        {
            return isAdmin.Error;
        }

        return isAdmin.Value
            ? await keycloak.RemoveOrganizationGroupMemberAsync(organizationId, admins.Id, userId, cancellationToken).ConfigureAwait(false)
            : Result.Success();
    }

    /// <summary>
    /// The top-level group named exactly <c>admins</c>. Found among the top-level listing (not by name search,
    /// which also matches nested groups), because a top-level organization group's <c>parentId</c> is the
    /// organization's internal backing group and cannot tell levels apart.
    /// </summary>
    private async Task<Result<KeycloakGroup?>> FindAsync(string organizationId, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<KeycloakGroup>> found = await keycloak.ListOrganizationTopLevelGroupsAsync(organizationId, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess
            ? found.Value.FirstOrDefault(static g => string.Equals(g.Name, GroupName, StringComparison.Ordinal))
            : found.Error;
    }

    /// <summary>Membership by group <b>id</b>; a user who is not a member of the organization is in none of its groups.</summary>
    private async Task<Result<bool>> IsInGroupAsync(string organizationId, string groupId, string userId, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<KeycloakGroup>> groups = await keycloak.ListMemberOrganizationGroupsAsync(organizationId, userId, cancellationToken).ConfigureAwait(false);
        if (!groups.IsSuccess)
        {
            return groups.Error.Kind == ErrorKind.NotFound ? false : groups.Error;
        }

        return groups.Value.Any(g => string.Equals(g.Id, groupId, StringComparison.Ordinal));
    }

    private async Task<Result<string>> EnsureGroupAsync(string organizationId, CancellationToken cancellationToken)
    {
        Result<KeycloakGroup?> existing = await FindAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return existing.Error;
        }

        if (existing.Value is { } group)
        {
            return group.Id;
        }

        Result<string> created = await keycloak.CreateOrganizationGroupAsync(organizationId, GroupName, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            if (created.Error.Kind != ErrorKind.Conflict)
            {
                return created.Error;
            }

            // Lost a race with another grant: use the group that won.
            Result<KeycloakGroup?> winner = await FindAsync(organizationId, cancellationToken).ConfigureAwait(false);
            return !winner.IsSuccess ? winner.Error
                : winner.Value is { } won ? won.Id
                : created.Error;
        }

        LogGroupCreated(logger, organizationId, created.Value);
        await MapEntityAdminRoleAsync(organizationId, created.Value, cancellationToken).ConfigureAwait(false);
        return created.Value;
    }

    /// <summary>
    /// Best effort: the role is only a hint in tokens (scope is resolved from group membership), so a failure
    /// is logged for the operator and never fails the grant — failing it would leave a group that the next
    /// grant reuses without retrying the mapping anyway.
    /// </summary>
    private async Task MapEntityAdminRoleAsync(string organizationId, string groupId, CancellationToken cancellationToken)
    {
        Result<KeycloakRole> role = await keycloak.GetRealmRoleAsync(KeycloakAdminNames.EntityAdminRole, cancellationToken).ConfigureAwait(false);
        Result mapped = role.IsSuccess
            ? await keycloak.AddOrganizationGroupRealmRolesAsync(organizationId, groupId, [role.Value], cancellationToken).ConfigureAwait(false)
            : role.Error;
        if (!mapped.IsSuccess)
        {
            LogRoleNotMapped(logger, KeycloakAdminNames.EntityAdminRole, organizationId, mapped.Error.Kind);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the admins group {GroupId} of organization {OrganizationId}")]
    private static partial void LogGroupCreated(ILogger logger, string organizationId, string groupId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not map realm role {Role} onto the admins group of organization {OrganizationId} ({Kind}); map it in Keycloak's console")]
    private static partial void LogRoleNotMapped(ILogger logger, string role, string organizationId, ErrorKind kind);
}
