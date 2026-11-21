using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Adapters.Subscriptions;

/// <summary>The store and endpoint when the server has no subscription store configured: every call is 501.</summary>
internal sealed class UnconfiguredSubscriptions : ISubscriptionStore, INotificationEndpoint
{
    public static readonly UnconfiguredSubscriptions Instance = new();

    private static Error NotConfigured => Error.Unsupported("Change notifications are not configured on this server.");

    public Task<Result<Subscription>> CreateAsync(Subscription subscription, string refreshToken, CancellationToken cancellationToken) =>
        Task.FromResult<Result<Subscription>>(NotConfigured);

    public Task<Result<Subscription>> GetAsync(string creatorId, string id, CancellationToken cancellationToken) =>
        Task.FromResult<Result<Subscription>>(NotConfigured);

    public Task<Result<IReadOnlyList<Subscription>>> ListAsync(string creatorId, CancellationToken cancellationToken) =>
        Task.FromResult<Result<IReadOnlyList<Subscription>>>(NotConfigured);

    public Task<Result<Subscription>> UpdateAsync(Subscription subscription, string? refreshToken, CancellationToken cancellationToken) =>
        Task.FromResult<Result<Subscription>>(NotConfigured);

    public Task<Result> DeleteAsync(string creatorId, string id, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(NotConfigured));

    public Task<Result<IReadOnlyList<LeasedSubscription>>> LeaseDueAsync(DateTimeOffset now, TimeSpan lease, int max, CancellationToken cancellationToken) =>
        Task.FromResult<Result<IReadOnlyList<LeasedSubscription>>>(NotConfigured);

    public Task<Result> CompletePollAsync(
        string id, string? syncState, string? refreshToken, DateTimeOffset? polledAt, DateTimeOffset nextPoll, int failures, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(NotConfigured));

    public Task<Result<IReadOnlyList<Subscription>>> RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<Result<IReadOnlyList<Subscription>>>(NotConfigured);

    public Task<Result> ValidateAsync(Uri notificationUrl, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(NotConfigured));

    public Task<Result> DeliverAsync(Uri notificationUrl, IReadOnlyList<ChangeNotification> notifications, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(NotConfigured));

    public Task<Result> DeliverLifecycleAsync(Uri lifecycleNotificationUrl, LifecycleNotification notification, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(NotConfigured));
}
