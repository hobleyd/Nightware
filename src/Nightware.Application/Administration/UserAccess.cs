using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration;

/// <summary>A user the actor may act on, with the backend links derived for them.</summary>
public sealed record UserDetail(ManagedUser User, IReadOnlyList<BackendLink> Links)
{
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
    IGroupwareAccounts groupware)
{
    public async Task<Result<ManagedUser>> LoadAsync(string userId, CancellationToken cancellationToken)
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

        Result allowed = AdminAuthorization.RequireMayManageUser(actor.Current, memberships.Value, scope.Value);
        if (!allowed.IsSuccess)
        {
            // Not "forbidden": an entity admin must not learn which ids exist outside their scope.
            return actor.Current.IsGlobal ? allowed.Error : Error.NotFound("The user was not found.");
        }

        return new ManagedUser(identity.Value, memberships.Value.Count > 0 ? memberships.Value[0] : null, scope.Value.IsGlobal, scope.Value.EntityIds.Count > 0);
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

        BackendLink mailLink = LinkStateEvaluator.Mail(identity.Email, await mailbox.ConfigureAwait(false));
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
