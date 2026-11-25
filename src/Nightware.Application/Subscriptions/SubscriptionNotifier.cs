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
/// delivered to its endpoint. The state advances only after a 2xx, so delivery is at least once. A tracked
/// subscription's upserts are <c>created</c> or <c>updated</c> by whether it has seen the item (<see cref="ISubscriptionItems"/>);
/// what it has seen advances with the state, so a retried delivery classifies the same way.
/// </summary>
public sealed class SubscriptionNotifier(ChangeFeed feed, INotificationEndpoint endpoint, ISubscriptionItems items)
{
    public async Task<Result<PollOutcome>> PollAsync(Subscription subscription, DateTimeOffset lastPoll, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (subscription.SyncState is not { } state)
        {
            return await StartAsync(subscription, cancellationToken).ConfigureAwait(false);
        }

        var batch = await feed.ChangesAsync(subscription.Target, state, lastPoll, cancellationToken).ConfigureAwait(false);
        if (!batch.IsSuccess)
        {
            return batch.Error;
        }

        var changes = batch.Value.Classified
            ? batch.Value.Changes
            : await ClassifyAsync(subscription, batch.Value.Changes, cancellationToken).ConfigureAwait(false);
        var notifications = changes
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

        if (batch.Value.Classified)
        {
            return new PollOutcome(batch.Value.State, notifications.Count);
        }

        // Best effort: if this fails, an item seen now reads as created again on a later change.
        await items.RecordAsync(
            subscription.Id,
            [.. changes.Where(static c => c.ChangeType == SubscriptionChangeType.Created).Select(static c => c.Id)],
            [.. changes.Where(static c => c.ChangeType == SubscriptionChangeType.Deleted).Select(static c => c.Id)],
            cancellationToken).ConfigureAwait(false);
        return new PollOutcome(batch.Value.State, notifications.Count);
    }

    /// <summary>The feed's guess, corrected by what the subscription has seen when it is tracked.</summary>
    private async Task<IReadOnlyList<ResourceChange>> ClassifyAsync(Subscription subscription, IReadOnlyList<ResourceChange> changes, CancellationToken cancellationToken)
    {
        var upserted = changes.Where(static c => c.ChangeType != SubscriptionChangeType.Deleted).Select(static c => c.Id).Distinct(StringComparer.Ordinal).ToList();
        if (upserted.Count == 0)
        {
            return changes;
        }

        var known = await items.KnownAsync(subscription.Id, upserted, cancellationToken).ConfigureAwait(false);
        if (known is not { IsSuccess: true, Value: { } seen })
        {
            return changes;
        }

        // An item changed twice in one batch is created once, then updated.
        var fresh = new HashSet<string>(StringComparer.Ordinal);
        return [.. changes.Select(c => c.ChangeType == SubscriptionChangeType.Deleted
            ? c
            : c with { ChangeType = seen.Contains(c.Id) || !fresh.Add(c.Id) ? SubscriptionChangeType.Updated : SubscriptionChangeType.Created })];
    }

    /// <summary>The baseline: the state to start from, and every item that exists now as seen.</summary>
    private async Task<Result<PollOutcome>> StartAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        var baseline = await feed.BaselineAsync(subscription.Target, cancellationToken).ConfigureAwait(false);
        if (!baseline.IsSuccess)
        {
            return baseline.Error;
        }

        await items.ReplaceAsync(subscription.Id, baseline.Value.Ids, cancellationToken).ConfigureAwait(false);
        return new PollOutcome(baseline.Value.State, 0);
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

        return await StartAsync(subscription, cancellationToken).ConfigureAwait(false);
    }

    internal static ChangeNotification ToNotification(Subscription subscription, ResourceChange change)
    {
        if (change.Resource is { } resource)
        {
            return new ChangeNotification(subscription.Id, subscription.ExpirationDateTime, change.ChangeType.ToString().ToLowerInvariant(), resource, change.ODataType ?? string.Empty, change.Id)
            {
                ClientState = subscription.ClientState,
            };
        }

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
