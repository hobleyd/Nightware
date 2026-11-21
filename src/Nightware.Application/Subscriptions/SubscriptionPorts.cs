using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Application.Subscriptions;

/// <summary>
/// The subscription store (ADR 0006 D10): subscriptions, their sync state and a sealed refresh credential per
/// subscription. When the server has none configured every call answers <see cref="ErrorKind.Unsupported"/>.
/// </summary>
public interface ISubscriptionStore
{
    /// <param name="refreshToken">Sealed at rest by the store; never returned by a read.</param>
    Task<Result<Subscription>> CreateAsync(Subscription subscription, string refreshToken, CancellationToken cancellationToken);

    Task<Result<Subscription>> GetAsync(string creatorId, string id, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<Subscription>>> ListAsync(string creatorId, CancellationToken cancellationToken);

    /// <param name="refreshToken">A new credential to seal in place of the old one; null keeps it.</param>
    Task<Result<Subscription>> UpdateAsync(Subscription subscription, string? refreshToken, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(string creatorId, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Leases up to <paramref name="max"/> unexpired subscriptions that are due a poll, with their opened credentials.
    /// A lease keeps another instance from polling the same subscription until it lapses or is completed.
    /// </summary>
    Task<Result<IReadOnlyList<LeasedSubscription>>> LeaseDueAsync(DateTimeOffset now, TimeSpan lease, int max, CancellationToken cancellationToken);

    /// <summary>
    /// Ends a lease: the new sync state and credential (null keeps each), when the delivered state was read (null
    /// keeps the last poll time), when to poll next, and the failure count.
    /// </summary>
    Task<Result> CompletePollAsync(
        string id, string? syncState, string? refreshToken, DateTimeOffset? polledAt, DateTimeOffset nextPoll, int failures, CancellationToken cancellationToken);

    /// <summary>Removes subscriptions that expired before <paramref name="now"/> (with their credentials) and returns them.</summary>
    Task<Result<IReadOnlyList<Subscription>>> RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <param name="RefreshToken">Opened from its seal; null when it no longer opens (the key changed).</param>
/// <param name="Failures">Consecutive failed polls or deliveries, for backoff.</param>
public sealed record LeasedSubscription(Subscription Subscription, string? RefreshToken, DateTimeOffset LastPoll, int Failures);

/// <summary>A fresh access token for a subscriber, and the refresh token to keep for next time.</summary>
public sealed record SubscriberSession(string AccessToken, string RefreshToken)
{
    public override string ToString() => "SubscriberSession ***";
}

/// <summary>Renews a subscriber's session from their stored refresh token, when no request is in flight.</summary>
public interface ISubscriberSessions
{
    /// <returns><see cref="ErrorKind.BackendAuthFailed"/> when the session has ended: the subscriber must reauthorize.</returns>
    Task<Result<SubscriberSession>> RenewAsync(string refreshToken, CancellationToken cancellationToken);
}

/// <summary>A Graph <c>changeNotification</c> (without resource data).</summary>
/// <param name="Resource">The item's Graph path, e.g. <c>Users/{user}/Messages/{id}</c>.</param>
/// <param name="ODataType">e.g. <c>#Microsoft.Graph.Message</c>.</param>
public sealed record ChangeNotification(string SubscriptionId, DateTimeOffset SubscriptionExpirationDateTime, string ChangeType, string Resource, string ODataType, string Id)
{
    public string? ClientState { get; init; }
}

/// <summary>
/// The credential the notifier acts with when no request is in flight: a refresh token tied to the subscriber's
/// Keycloak session, obtained by exchanging their access token.
/// </summary>
public interface ISubscriberCredentials
{
    /// <summary>A refresh token for the caller of the current request.</summary>
    Task<Result<string>> IssueAsync(CancellationToken cancellationToken);
}

/// <summary>The subscriber's notification endpoint, reached only over the SSRF-guarded client.</summary>
public interface INotificationEndpoint
{
    /// <summary>
    /// Graph's handshake: POST <c>?validationToken=…</c> must be answered 200 with the token as plain text within
    /// 10 seconds. An address that is not allowed (loopback, private, …) fails as a validation error.
    /// </summary>
    Task<Result> ValidateAsync(Uri notificationUrl, CancellationToken cancellationToken);

    /// <summary>POSTs a <c>changeNotificationCollection</c>; success only when the endpoint answers 2xx.</summary>
    Task<Result> DeliverAsync(Uri notificationUrl, IReadOnlyList<ChangeNotification> notifications, CancellationToken cancellationToken);

    /// <summary>POSTs one lifecycle notification (<c>reauthorizationRequired</c>, <c>missed</c>, <c>subscriptionRemoved</c>).</summary>
    Task<Result> DeliverLifecycleAsync(Uri lifecycleNotificationUrl, LifecycleNotification notification, CancellationToken cancellationToken);
}

/// <summary>A Graph lifecycle notification.</summary>
/// <param name="LifecycleEvent"><c>reauthorizationRequired</c>, <c>missed</c> or <c>subscriptionRemoved</c>.</param>
public sealed record LifecycleNotification(string SubscriptionId, DateTimeOffset SubscriptionExpirationDateTime, string LifecycleEvent)
{
    public const string ReauthorizationRequired = "reauthorizationRequired";
    public const string Missed = "missed";

    public string? ClientState { get; init; }

    public static LifecycleNotification For(Subscription subscription, string lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return new LifecycleNotification(subscription.Id, subscription.ExpirationDateTime, lifecycleEvent) { ClientState = subscription.ClientState };
    }
}
