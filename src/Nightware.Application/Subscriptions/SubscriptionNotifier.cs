using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Application.Subscriptions;

/// <summary>
/// The caller in the notifier's background scope: the subscriber, carrying the access token their stored refresh
/// token was just renewed for. Backend credentials are exchanged from it as from a request's token.
/// </summary>
public sealed class SubscriberContext
{
    public IUserContext? Current { get; private set; }

    public void ActAs(Principal principal, string accessToken) => Current = new Acting(principal, accessToken);

    private sealed record Acting(Principal Principal, string BearerToken) : IUserContext;
}

/// <summary>What one poll of a subscription did.</summary>
/// <param name="State">The sync state to keep: advanced only when the changes were delivered.</param>
/// <param name="Delivered">How many notifications the endpoint accepted.</param>
public sealed record PollOutcome(string? State, int Delivered);

/// <summary>
/// One poll of one subscription, run as the subscriber: what changed since its state, filtered to its change types,
/// delivered to its endpoint. The state advances only after a 2xx, so delivery is at least once.
/// </summary>
public sealed class SubscriptionNotifier(ChangeFeed feed, INotificationEndpoint endpoint)
{
    public async Task<Result<PollOutcome>> PollAsync(Subscription subscription, DateTimeOffset lastPoll, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (subscription.SyncState is not { } state)
        {
            var baseline = await feed.BaselineAsync(subscription.Target, cancellationToken).ConfigureAwait(false);
            return baseline.IsSuccess ? new PollOutcome(baseline.Value, 0) : baseline.Error;
        }

        var batch = await feed.ChangesAsync(subscription.Target, state, lastPoll, cancellationToken).ConfigureAwait(false);
        if (!batch.IsSuccess)
        {
            return batch.Error;
        }

        var notifications = batch.Value.Changes
            .Where(c => subscription.ChangeType.HasFlag(c.ChangeType))
            .Select(c => ToNotification(subscription, c))
            .ToList();
        if (notifications.Count > 0)
        {
            var delivered = await endpoint.DeliverAsync(subscription.NotificationUrl, notifications, cancellationToken).ConfigureAwait(false);
            if (!delivered.IsSuccess)
            {
                return delivered.Error;
            }
        }

        return new PollOutcome(batch.Value.State, notifications.Count);
    }

    /// <summary>
    /// After its state expired: tells the subscriber (<c>missed</c>, on its lifecycle URL) and starts again from now.
    /// Changes in the gap are not replayed; the client resynchronises, as Graph asks it to.
    /// </summary>
    public async Task<Result<PollOutcome>> RecoverAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (subscription.LifecycleNotificationUrl is { } lifecycle)
        {
            await endpoint.DeliverLifecycleAsync(lifecycle, LifecycleNotification.For(subscription, LifecycleNotification.Missed), cancellationToken).ConfigureAwait(false);
        }

        var baseline = await feed.BaselineAsync(subscription.Target, cancellationToken).ConfigureAwait(false);
        return baseline.IsSuccess ? new PollOutcome(baseline.Value, 0) : baseline.Error;
    }

    internal static ChangeNotification ToNotification(Subscription subscription, ResourceChange change)
    {
        var user = $"Users/{subscription.Target.Mailbox.Address}";
        var (path, type) = subscription.Target.Kind switch
        {
            SubscriptionResourceKind.Messages => ($"{user}/Messages/{change.Id}", "#Microsoft.Graph.Message"),
            SubscriptionResourceKind.Events => ($"{user}/Events/{change.Id}", "#Microsoft.Graph.Event"),
            SubscriptionResourceKind.Contacts => ($"{user}/Contacts/{change.Id}", "#Microsoft.Graph.Contact"),
            _ => ($"{user}/todo/lists/{subscription.Target.Folder}/tasks/{change.Id}", "#Microsoft.Graph.TodoTask"),
        };
        return new ChangeNotification(subscription.Id, subscription.ExpirationDateTime, change.ChangeType.ToString().ToLowerInvariant(), path, type, change.Id)
        {
            ClientState = subscription.ClientState,
        };
    }
}
