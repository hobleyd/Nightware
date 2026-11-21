using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Adapters.Subscriptions;

/// <summary>
/// The change notifier (ADR 0006 D10): every <see cref="SubscriptionOptions.PollInterval"/> it leases the subscriptions
/// that are due, renews each subscriber's session from their sealed refresh token, and polls their resource as them in
/// a scope of its own. Off unless the subscription store is configured. Failures back off per subscription; nothing
/// here can stop the host.
/// </summary>
internal sealed partial class SubscriptionPoller(IServiceScopeFactory scopes, IOptions<SubscriptionOptions> options, TimeProvider time, ILogger<SubscriptionPoller> logger)
    : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsConfigured)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.PollInterval, time);
        do
        {
            try
            {
                await PollDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogPollFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task PollDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISubscriptionStore>();

        // Expired subscriptions go, with their sealed credentials (Graph sends nothing when one simply expires).
        var expired = await store.RemoveExpiredAsync(time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (expired is { IsSuccess: true, Value.Count: > 0 })
        {
            LogExpired(logger, expired.Value.Count);
        }

        var due = await store.LeaseDueAsync(time.GetUtcNow(), Lease, BatchSize, cancellationToken).ConfigureAwait(false);
        if (!due.IsSuccess)
        {
            LogLeaseFailed(logger, due.Error.Message);
            return;
        }

        foreach (var leased in due.Value)
        {
            await PollOneAsync(store, leased, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PollOneAsync(ISubscriptionStore store, LeasedSubscription leased, CancellationToken cancellationToken)
    {
        var subscription = leased.Subscription;
        var polledAt = time.GetUtcNow();
        using var scope = scopes.CreateScope();
        var session = leased.RefreshToken is { } refresh
            ? await scope.ServiceProvider.GetRequiredService<ISubscriberSessions>().RenewAsync(refresh, cancellationToken).ConfigureAwait(false)
            : Result<SubscriberSession>.Failure(Error.BackendAuthFailed("The subscription's credential no longer opens."));
        if (!session.IsSuccess)
        {
            LogSessionFailed(logger, subscription.Id, session.Error.Kind);
            if (session.Error.Kind is ErrorKind.BackendAuthFailed or ErrorKind.Unauthorized && leased.Failures == 0)
            {
                // Once, on the first failure: the client renews (PATCH) with a fresh token to reauthorize.
                await LifecycleAsync(scope, subscription, LifecycleNotification.ReauthorizationRequired, cancellationToken).ConfigureAwait(false);
            }

            await BackOffAsync(store, leased, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var mailbox = subscription.Target.Mailbox.Address;
        scope.ServiceProvider.GetRequiredService<SubscriberContext>().ActAs(new Principal(subscription.CreatorId, mailbox, mailbox, mailbox), session.Value.AccessToken);
        var notifier = scope.ServiceProvider.GetRequiredService<SubscriptionNotifier>();
        var outcome = await notifier.PollAsync(subscription, leased.LastPoll, cancellationToken).ConfigureAwait(false);
        if (outcome is { IsSuccess: false, Error.Kind: ErrorKind.SyncStateGone })
        {
            LogMissed(logger, subscription.Id);
            outcome = await notifier.RecoverAsync(subscription, cancellationToken).ConfigureAwait(false);
        }

        if (!outcome.IsSuccess)
        {
            LogNotifyFailed(logger, subscription.Id, outcome.Error.Kind);
            await BackOffAsync(store, leased, session.Value.RefreshToken, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (outcome.Value.Delivered > 0)
        {
            LogDelivered(logger, outcome.Value.Delivered, subscription.Id);
        }

        await store.CompletePollAsync(subscription.Id, outcome.Value.State, session.Value.RefreshToken, polledAt, time.GetUtcNow() + options.Value.PollInterval, 0, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task LifecycleAsync(IServiceScope scope, Subscription subscription, string lifecycleEvent, CancellationToken cancellationToken)
    {
        if (subscription.LifecycleNotificationUrl is { } url)
        {
            await scope.ServiceProvider.GetRequiredService<INotificationEndpoint>()
                .DeliverLifecycleAsync(url, LifecycleNotification.For(subscription, lifecycleEvent), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Keeps the state (so nothing is skipped), keeps the newest credential, and waits longer each time.</summary>
    private async Task BackOffAsync(ISubscriptionStore store, LeasedSubscription leased, string? refreshToken, CancellationToken cancellationToken)
    {
        var failures = leased.Failures + 1;
        var wait = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, options.Value.PollInterval.Ticks * (1L << Math.Min(failures, 16))));
        await store.CompletePollAsync(leased.Subscription.Id, null, refreshToken, null, time.GetUtcNow() + wait, failures, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Change-notification poll failed")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not lease subscriptions: {Reason}")]
    private static partial void LogLeaseFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription {SubscriptionId}: the subscriber's session could not be renewed ({Kind})")]
    private static partial void LogSessionFailed(ILogger logger, string subscriptionId, ErrorKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription {SubscriptionId}: poll or delivery failed ({Kind})")]
    private static partial void LogNotifyFailed(ILogger logger, string subscriptionId, ErrorKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} expired subscription(s)")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription {SubscriptionId}: its state expired; starting again from now")]
    private static partial void LogMissed(ILogger logger, string subscriptionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivered {Count} change notification(s) for subscription {SubscriptionId}")]
    private static partial void LogDelivered(ILogger logger, int count, string subscriptionId);
}
