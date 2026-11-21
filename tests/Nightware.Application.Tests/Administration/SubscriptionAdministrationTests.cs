using Nightware.Application.Administration.Subscriptions;
using Nightware.Application.Subscriptions;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Application.Tests.Administration;

public sealed class SubscriptionAdministrationTests
{
    private readonly AdminTestBed _bed = new();
    private readonly IChangeSubscriptions _subscriptions = Substitute.For<IChangeSubscriptions>();
    private readonly INotificationEndpoint _endpoint = Substitute.For<INotificationEndpoint>();

    private static CancellationToken Ct => AdminTestBed.Ct;

    private static Subscription Calendar(string id, string creator, Uri? lifecycle = null) => new(
        id, creator, "me/events", new SubscriptionTarget(new MailboxRef("carol@acme.example"), SubscriptionResourceKind.Events, null),
        SubscriptionChangeType.Created, new Uri("https://hooks.example.com/n"), new DateTimeOffset(2026, 11, 30, 0, 0, 0, TimeSpan.Zero))
    {
        LifecycleNotificationUrl = lifecycle,
        ApplicationId = "app",
    };

    private RemoveSubscription Remove() => new(_bed.Actor, _bed.UserAccess(), _subscriptions, _endpoint, _bed.Audit);

    [Fact]
    public void Health_reads_from_reauthorization_then_failures()
    {
        var sub = Calendar("s1", "u-carol");
        new ManagedSubscription(sub, null, 0, null).Health.ShouldBe(SubscriptionHealth.Healthy);
        new ManagedSubscription(sub, null, 3, null).Health.ShouldBe(SubscriptionHealth.Failing);
        new ManagedSubscription(sub, null, 3, DateTimeOffset.UnixEpoch).Health.ShouldBe(SubscriptionHealth.WaitingForReauthorization);
    }

    [Fact]
    public async Task A_users_subscriptions_are_listed_for_whoever_may_see_the_user()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example");
        _subscriptions.ListAsync("u-carol", Ct).Returns(Result<IReadOnlyList<ManagedSubscription>>.Success([new ManagedSubscription(Calendar("s1", "u-carol"), null, 0, null)]));

        (await new ListUserSubscriptions(_bed.UserAccess(), _subscriptions).ExecuteAsync("u-carol", Ct)).Value.ShouldHaveSingleItem();

        _bed.ActAsNobody();
        (await new ListUserSubscriptions(_bed.UserAccess(), _subscriptions).ExecuteAsync("u-carol", Ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Removing_one_tells_the_app_and_is_audited()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example");
        var sub = Calendar("s1", "u-carol", new Uri("https://hooks.example.com/life"));
        _subscriptions.ListAsync("u-carol", Ct).Returns(Result<IReadOnlyList<ManagedSubscription>>.Success([new ManagedSubscription(sub, null, 0, null)]));
        _subscriptions.RemoveAsync("s1", Ct).Returns(sub);

        (await Remove().ExecuteAsync("u-carol", "s1", Ct)).IsSuccess.ShouldBeTrue();

        await _endpoint.Received(1).DeliverLifecycleAsync(new Uri("https://hooks.example.com/life"), Arg.Is<LifecycleNotification>(n => n.LifecycleEvent == "subscriptionRemoved" && n.SubscriptionId == "s1"), Ct);
        _bed.Audit.Single("subscription.removed").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Another_users_subscription_cannot_be_removed_through_this_user()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example");
        _subscriptions.ListAsync("u-carol", Ct).Returns(Result<IReadOnlyList<ManagedSubscription>>.Success([]));

        (await Remove().ExecuteAsync("u-carol", "s-of-someone-else", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        await _subscriptions.DidNotReceiveWithAnyArgs().RemoveAsync(default!, Ct);
    }

    [Fact]
    public async Task Listing_everything_is_for_global_admins_and_names_each_person()
    {
        var carol = _bed.AddUser("u-carol", "carol", "carol@acme.example");
        _subscriptions.ListAsync(null, Ct).Returns(Result<IReadOnlyList<ManagedSubscription>>.Success(
            [new ManagedSubscription(Calendar("s1", "u-carol"), null, 0, null), new ManagedSubscription(Calendar("s2", "u-gone"), null, 0, null)]));
        var list = new ListAllSubscriptions(_bed.Actor, _subscriptions, _bed.Identities);

        _bed.ActAsEntityAdmin();
        (await list.ExecuteAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        _bed.ActAsGlobal();
        var all = (await list.ExecuteAsync(Ct)).Value;
        all.Select(o => o.User?.Id).ShouldBe(["u-carol", null]);
        all[0].User.ShouldBe(carol);
    }
}
