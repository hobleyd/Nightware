using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Users;

/// <summary>
/// Adds an entry to a user's access history (on the user in Keycloak): what was done, by which administrator, and
/// what each system said straight after, so the admin can see later that an offboarding shut every way in. A
/// history that cannot be written never fails or undoes the action it describes; the audit log still has it.
/// </summary>
public sealed class AccessHistoryRecorder(IAdminActor actor, IIdentityAccounts identities, TimeProvider time)
{
    public async Task RecordAsync(string userId, AccessEventKind kind, UserDetail? after, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result<IdentityAccount> admin = await identities.GetAsync(actor.Current.UserId, cancellationToken).ConfigureAwait(false);
        string by = admin.IsSuccess ? admin.Value.Username : actor.Current.UserId;
        // Sign-in included: whether Keycloak itself was shut is the first thing an offboarding's history must say.
        SystemState[] systems = after is null ? [] : [.. after.Links.Select(static l => new SystemState(l.Backend, l.State))];
        await identities.AddAccessEventAsync(userId, new AccessEvent(time.GetUtcNow(), kind, by, systems), cancellationToken).ConfigureAwait(false);
    }

    /// <summary><paramref name="user"/> as it stands once <paramref name="backend"/>'s account is deleted.</summary>
    public static UserDetail WithGone(UserDetail user, BackendKind backend)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user with { Links = [.. user.Links.Select(l => l.Backend == backend ? new BackendLink(backend, LinkState.Missing) : l)] };
    }
}
