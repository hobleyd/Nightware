using Nightware.Adapters.Subscriptions;
using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Adapters.Subscriptions.Tests;

public sealed class UnconfiguredSubscriptionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Uri Url = new("https://hooks.example.com/n");

    private static readonly Subscription Sub = new(
        "s1", "u1", "me/events", new SubscriptionTarget(new MailboxRef("a@example.com"), SubscriptionResourceKind.Events, null),
        SubscriptionChangeType.Created, Url, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Every_call_is_501()
    {
        var none = UnconfiguredSubscriptions.Instance;
        var now = DateTimeOffset.UnixEpoch;

        Error?[] errors =
        [
            (await none.CreateAsync(Sub, "r", Ct)).Error,
            (await none.GetAsync("u1", "s1", Ct)).Error,
            (await none.ListAsync("u1", Ct)).Error,
            (await none.UpdateAsync(Sub, null, Ct)).Error,
            (await none.DeleteAsync("u1", "s1", Ct)).Error,
            (await none.LeaseDueAsync(now, TimeSpan.FromMinutes(1), 1, Ct)).Error,
            (await none.CompletePollAsync("s1", null, null, null, now, 0, Ct)).Error,
            (await none.RemoveExpiredAsync(now, Ct)).Error,
            (await none.ValidateAsync(Url, Ct)).Error,
            (await none.DeliverAsync(Url, [], Ct)).Error,
            (await none.DeliverLifecycleAsync(Url, new LifecycleNotification("s1", now, LifecycleNotification.Missed), Ct)).Error,
        ];

        errors.ShouldAllBe(e => e != null && e.Kind == ErrorKind.Unsupported);
    }
}
