using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration;

/// <summary>A user the actor may act on, with the backend links derived for them.</summary>
public sealed record UserDetail(ManagedUser User, IReadOnlyList<BackendLink> Links)
{
    /// <summary>
    /// Why the console only shows this user and changes nothing (they administer Keycloak, the mail or the Nextcloud
    /// server); null when the actor may act on them. Set only by <see cref="UserAccess.ViewWithLinksAsync"/>.
    /// </summary>
    public string? OffLimits { get; init; }

    public BackendLink For(BackendKind backend) => Links.Single(l => l.Backend == backend);
}

/// <summary>
/// Loads an existing user for a use case: the identity, their entities and admin scope, and the
/// <see cref="AdminAuthorization.RequireMayManageUser"/> check. Every use case that acts on a user goes
/// through here, so none can forget the check.
/// </summary>
public sealed class UserAccess(
    IAdminActor actor,
    IIdentityAccounts identities,
    IEntityRegistry entities,
    IAdminScopeSource scopes,
    IMailAccounts mail,
    IGroupwareAccounts groupware,
    AdministrationOptions options)
{
    public async Task<Result<ManagedUser>> LoadAsync(string userId, CancellationToken cancellationToken)
    {
        Result<(ManagedUser User, string? OffLimits)> loaded = await LoadAsync(userId, false, cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess ? loaded.Value.User : loaded.Error;
    }

    /// <summary>
    /// A user to show, not to act on: a global admin also sees an account the console never changes, with the
    /// reason in <see cref="UserDetail.OffLimits"/>, so a conflict the console lists always opens. Entity admins
    /// get exactly what <see cref="LoadAsync(string, CancellationToken)"/> gives them.
    /// </summary>
    public async Task<Result<UserDetail>> ViewWithLinksAsync(string userId, CancellationToken cancellationToken)
    {
        Result<(ManagedUser User, string? OffLimits)> loaded = await LoadAsync(userId, true, cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess
            ? await WithLinksAsync(loaded.Value.User, cancellationToken).ConfigureAwait(false) with { OffLimits = loaded.Value.OffLimits }
            : loaded.Error;
    }

    private async Task<Result<(ManagedUser User, string? OffLimits)>> LoadAsync(string userId, bool view, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result admin = AdminAuthorization.RequireAdmin(actor.Current);
        if (!admin.IsSuccess)
        {
            return admin.Error;
        }

        Result<IdentityAccount> identity = await identities.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!identity.IsSuccess)
        {
            return identity.Error;
        }

        Result<IReadOnlyList<string>> memberships = await entities.GetMembershipsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!memberships.IsSuccess)
        {
            return memberships.Error;
        }

        Result<AdminScope> scope = await scopes.ResolveAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        // Only a view by a global admin gets past a refusal, and then only to read.
        bool showOnly = view && actor.Current.IsGlobal;
        string? offLimits = null;
        Result allowed = AdminAuthorization.RequireMayManageUser(actor.Current, memberships.Value, scope.Value, options.GlobalAdminsAdministerServers);
        if (!allowed.IsSuccess && showOnly)
        {
            offLimits = allowed.Error.Message;
        }
        else if (!allowed.IsSuccess)
        {
            // Not "forbidden": an entity admin must not learn which ids exist outside their scope.
            return actor.Current.IsGlobal ? allowed.Error : Error.NotFound("The user was not found.");
        }

        // Administrators of the mail or groupware server are as off-limits as Keycloak's own: resetting their
        // password would hand the actor that server's administration. A global admin's mailbox, holding the
        // administrator role their role brings, is not, to another global admin, who holds that role already.
        Task<Result<MailAccount>> mailbox = mail.FindByAddressAsync(identity.Value.Email, cancellationToken);
        Task<Result<GroupwareAccount>> cloud = groupware.FindAsync(identity.Value.Username, cancellationToken);
        await Task.WhenAll(mailbox, cloud).ConfigureAwait(false);
        bool ownAdministratorRole = options.GlobalAdminsAdministerServers && actor.Current.IsGlobal && scope.Value.IsGlobal;
        if ((await mailbox.ConfigureAwait(false)) is { IsSuccess: true, Value.IsPrivileged: true } privileged
                && !(ownAdministratorRole && privileged.Value.IsAdministrator)
            || (await cloud.ConfigureAwait(false)) is { IsSuccess: true, Value.IsAdministrator: true })
        {
            const string ServerAdministrator = "This account administers the mail or Nextcloud server; manage it there.";
            if (showOnly)
            {
                offLimits ??= ServerAdministrator;
            }
            else
            {
                return actor.Current.IsGlobal
                    ? Error.Forbidden(ServerAdministrator)
                    : Error.NotFound("The user was not found.");
            }
        }

        return (new ManagedUser(identity.Value, memberships.Value.Count > 0 ? memberships.Value[0] : null, scope.Value.IsGlobal, scope.Value.EntityIds.Count > 0), offLimits);
    }

    public async Task<Result<UserDetail>> LoadWithLinksAsync(string userId, CancellationToken cancellationToken)
    {
        Result<ManagedUser> user = await LoadAsync(userId, cancellationToken).ConfigureAwait(false);
        return user.IsSuccess ? await WithLinksAsync(user.Value, cancellationToken).ConfigureAwait(false) : user.Error;
    }

    public async Task<UserDetail> WithLinksAsync(ManagedUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        IdentityAccount identity = user.Identity;
        Task<Result<MailAccount>> mailbox = mail.FindByAddressAsync(identity.Email, cancellationToken);
        Task<Result<GroupwareAccount>> cloud = groupware.FindAsync(identity.Username, cancellationToken);
        await Task.WhenAll(mailbox, cloud).ConfigureAwait(false);

        BackendLink mailLink = LinkStateEvaluator.Mail(identity.Email, await mailbox.ConfigureAwait(false), options.GlobalAdminsAdministerServers && user.IsGlobalAdmin);
        if (mailLink.State == LinkState.Missing && identity.MailDeletedAt is { } mailDeleted)
        {
            mailLink = mailLink with { Detail = $"Mail deleted on {mailDeleted:yyyy-MM-dd HH:mm} UTC." };
        }

        BackendLink cloudLink = LinkStateEvaluator.Groupware(identity.Username, identity.Email, await cloud.ConfigureAwait(false));
        if (cloudLink.State == LinkState.Missing && identity.GroupwareDeletedAt is { } cloudDeleted)
        {
            cloudLink = cloudLink with { Detail = $"Nextcloud data deleted on {cloudDeleted:yyyy-MM-dd HH:mm} UTC." };
        }

        return new UserDetail(user, [LinkStateEvaluator.Identity(identity), mailLink, cloudLink]);
    }
}
