using Nightware.Application.Subscriptions;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Application.Administration.Subscriptions;

/// <summary>A change-notification subscription as the console shows it: the subscription and how its polling goes.</summary>
/// <param name="LastPoll">When its changes were last read; null before the first poll.</param>
/// <param name="Failures">Consecutive failed polls or deliveries.</param>
/// <param name="ReauthorizationRequestedAt">When the app was asked to reauthorize and has not yet.</param>
public sealed record ManagedSubscription(Subscription Subscription, DateTimeOffset? LastPoll, int Failures, DateTimeOffset? ReauthorizationRequestedAt)
{
    public SubscriptionHealth Health =>
        ReauthorizationRequestedAt is not null ? SubscriptionHealth.WaitingForReauthorization
        : Failures > 0 ? SubscriptionHealth.Failing
        : SubscriptionHealth.Healthy;
}

public enum SubscriptionHealth
{
    Healthy,
    Failing,
    WaitingForReauthorization,
}

/// <summary>
/// The change-notification subscriptions (ADR 0006 D10) as the console administers them, in the façade's subscription
/// store. <see cref="ErrorKind.Unsupported"/> when the console has no store configured.
/// </summary>
public interface IChangeSubscriptions
{
    /// <param name="creatorId">The Keycloak user whose subscriptions to list; null for every subscription.</param>
    Task<Result<IReadOnlyList<ManagedSubscription>>> ListAsync(string? creatorId, CancellationToken cancellationToken);

    /// <summary>Deletes the subscription and its sealed credential, and returns it. NotFound when it is gone.</summary>
    Task<Result<Subscription>> RemoveAsync(string id, CancellationToken cancellationToken);
}

/// <summary>One user's subscriptions, for whoever may see the user.</summary>
public sealed class ListUserSubscriptions(UserAccess users, IChangeSubscriptions subscriptions)
{
    public async Task<Result<IReadOnlyList<ManagedSubscription>>> ExecuteAsync(string userId, CancellationToken cancellationToken)
    {
        Result<ManagedUser> user = await users.LoadAsync(userId, cancellationToken).ConfigureAwait(false);
        return user.IsSuccess ? await subscriptions.ListAsync(userId, cancellationToken).ConfigureAwait(false) : user.Error;
    }
}

/// <summary>A subscription and the person it acts for.</summary>
public sealed record SubscriptionOwner(ManagedSubscription Subscription, IdentityAccount? User);

/// <summary>Every subscription on the server, with whom each acts for. Global administrators only.</summary>
public sealed class ListAllSubscriptions(IAdminActor actor, IChangeSubscriptions subscriptions, IIdentityAccounts identities)
{
    public async Task<Result<IReadOnlyList<SubscriptionOwner>>> ExecuteAsync(CancellationToken cancellationToken)
    {
        Result allowed = AdminAuthorization.RequireGlobal(actor.Current);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        Result<IReadOnlyList<ManagedSubscription>> all = await subscriptions.ListAsync(null, cancellationToken).ConfigureAwait(false);
        if (!all.IsSuccess)
        {
            return all.Error;
        }

        var people = new Dictionary<string, IdentityAccount?>(StringComparer.Ordinal);
        var owned = new List<SubscriptionOwner>(all.Value.Count);
        foreach (ManagedSubscription subscription in all.Value)
        {
            string creator = subscription.Subscription.CreatorId;
            if (!people.TryGetValue(creator, out IdentityAccount? person))
            {
                Result<IdentityAccount> found = await identities.GetAsync(creator, cancellationToken).ConfigureAwait(false);
                people[creator] = person = found.IsSuccess ? found.Value : null;
            }

            owned.Add(new SubscriptionOwner(subscription, person));
        }

        return owned;
    }
}

/// <summary>
/// Removes a user's subscription, and tells the app on its lifecycle URL (<c>subscriptionRemoved</c>, as Graph does
/// when it removes one). Audited.
/// </summary>
public sealed class RemoveSubscription(IAdminActor actor, UserAccess users, IChangeSubscriptions subscriptions, INotificationEndpoint endpoint, IAuditLog audit)
{
    public async Task<Result> ExecuteAsync(string userId, string subscriptionId, CancellationToken cancellationToken)
    {
        Result<ManagedUser> user = await users.LoadAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess)
        {
            return user.Error;
        }

        // Only one of this user's: an id alone must not reach someone else's subscription.
        Result<IReadOnlyList<ManagedSubscription>> theirs = await subscriptions.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!theirs.IsSuccess)
        {
            return theirs.Error;
        }

        if (!theirs.Value.Any(s => s.Subscription.Id == subscriptionId))
        {
            return Error.NotFound("The user has no such subscription.", "subscriptionId");
        }

        Result<Subscription> removed = await subscriptions.RemoveAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
        if (removed.IsSuccess && removed.Value.LifecycleNotificationUrl is { } lifecycle)
        {
            // Best effort: the subscription is gone either way.
            await endpoint.DeliverLifecycleAsync(lifecycle, LifecycleNotification.For(removed.Value, LifecycleNotification.SubscriptionRemoved), cancellationToken).ConfigureAwait(false);
        }

        audit.Record(new AuditEvent("subscription.removed", actor.Current.UserId, removed.IsSuccess)
        {
            TargetUserId = userId,
            EntityId = user.Value.EntityId,
            Details = new Dictionary<string, string?> { ["subscriptionId"] = subscriptionId, ["resource"] = removed.IsSuccess ? removed.Value.Resource : null },
        });
        return removed.ToResult();
    }
}
