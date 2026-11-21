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
}
