using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Nightware.Adapters.Subscriptions;
using Nightware.Application.Common.Identity;
using Nightware.Application.Contacts;
using Nightware.Application.Mail;
using Nightware.Application.Subscriptions;
using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.Subscriptions;

namespace Nightware.Adapters.Subscriptions.Tests;

public sealed class SubscriptionPollerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly ISubscriptionStore _store = Substitute.For<ISubscriptionStore>();
    private readonly ISubscriberSessions _sessions = Substitute.For<ISubscriberSessions>();
    private readonly IMailDeltaReader _mail = Substitute.For<IMailDeltaReader>();
    private readonly INotificationEndpoint _endpoint = Substitute.For<INotificationEndpoint>();
    private readonly TimeProvider _time = Substitute.For<TimeProvider>();
    private IUserContext? _actingAs;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Subscription Inbox => new(
        "s1", "sub-alice", "me/mailFolders('Inbox')/messages", new SubscriptionTarget(new MailboxRef("alice@example.com"), SubscriptionResourceKind.Messages, "Inbox"),
        SubscriptionChangeType.Created, new Uri("https://hooks.example.com/n"), Now.AddDays(1)) { SyncState = "t0" };

    public SubscriptionPollerTests()
    {
        _time.GetUtcNow().Returns(Now);
        _store.CompletePollAsync(default!, default, default, default, default, default, default).ReturnsForAnyArgs(Result.Success());
        _endpoint.DeliverAsync(Arg.Any<Uri>(), Arg.Any<IReadOnlyList<ChangeNotification>>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _endpoint.DeliverLifecycleAsync(Arg.Any<Uri>(), Arg.Any<LifecycleNotification>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _store.RemoveExpiredAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Subscription>>.Success([]));
    }

    private static readonly Uri Lifecycle = new("https://hooks.example.com/lifecycle");

    private SubscriptionPoller Poller(TimeProvider? time = null)
    {
        time ??= _time;
        var services = new ServiceCollection();
        services.AddSubscriptionsUseCases();
        services.AddSingleton(_store).AddSingleton(_sessions).AddSingleton(_endpoint).AddSingleton(time);
        services.AddSingleton(Substitute.For<IContactFolderStore>()).AddSingleton(Substitute.For<IContactDelta>())
            .AddSingleton(Substitute.For<ITaskDelta>()).AddSingleton(Substitute.For<IEventChangeFeed>());
        services.AddScoped<IUserContext>(sp => sp.GetRequiredService<SubscriberContext>().Current ?? throw new InvalidOperationException("no caller"));
        services.AddScoped(sp =>
        {
            _actingAs = sp.GetRequiredService<IUserContext>();
            return _mail;
        });
        var options = Options.Create(new SubscriptionOptions { ConnectionString = "x", SealingKey = "y", PollInterval = TimeSpan.FromSeconds(10) });
        return new SubscriptionPoller(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), options, time, NullLogger<SubscriptionPoller>.Instance);
    }

    private void Due(params LeasedSubscription[] leased) =>
        _store.LeaseDueAsync(Now, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<LeasedSubscription>>.Success(leased));

    [Fact]
    public async Task A_due_subscription_is_polled_as_its_subscriber_and_its_state_and_credential_kept()
    {
        Due(new LeasedSubscription(Inbox, "refresh-1", Now.AddSeconds(-10), 0));
        _sessions.RenewAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(new SubscriberSession("access-1", "refresh-2"));
        _mail.DeltaAsync(Arg.Any<MailboxRef>(), Arg.Any<FolderRef>(), new DeltaToken("t0"), Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.Done([new DeltaEntry<MailMessage>.Upsert(new MailMessage(new MessageId("m1")) { CreatedDateTime = Now })], new DeltaToken("t1")));

        await Poller().PollDueAsync(Ct);

        _actingAs!.BearerToken.ShouldBe("access-1");
        _actingAs.Principal.ShouldBe(new Principal("sub-alice", "alice@example.com", "alice@example.com", "alice@example.com"));
        await _endpoint.Received().DeliverAsync(Inbox.NotificationUrl, Arg.Is<IReadOnlyList<ChangeNotification>>(n => n.Single().Id == "m1"), Arg.Any<CancellationToken>());
        await _store.Received().CompletePollAsync("s1", "t1", "refresh-2", Now, Now.AddSeconds(10), 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_ended_session_backs_off_without_touching_the_state()
    {
        Due(new LeasedSubscription(Inbox, "refresh-1", Now, 2));
        _sessions.RenewAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(Error.BackendAuthFailed("session ended"));

        await Poller().PollDueAsync(Ct);

        await _store.Received().CompletePollAsync("s1", null, null, null, Now.AddSeconds(80), 3, Arg.Any<CancellationToken>());
        _endpoint.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_delivery_keeps_the_new_credential_but_not_the_new_state()
    {
        Due(new LeasedSubscription(Inbox, "refresh-1", Now, 0));
        _sessions.RenewAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(new SubscriberSession("access-1", "refresh-2"));
        _mail.DeltaAsync(Arg.Any<MailboxRef>(), Arg.Any<FolderRef>(), new DeltaToken("t0"), Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.Done([new DeltaEntry<MailMessage>.Upsert(new MailMessage(new MessageId("m1")) { CreatedDateTime = Now.AddSeconds(1) })], new DeltaToken("t1")));
        _endpoint.DeliverAsync(Arg.Any<Uri>(), Arg.Any<IReadOnlyList<ChangeNotification>>(), Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        await Poller().PollDueAsync(Ct);

        await _store.Received().CompletePollAsync("s1", null, "refresh-2", null, Now.AddSeconds(20), 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_credential_that_no_longer_opens_asks_for_reauthorization_and_backs_off()
    {
        Due(new LeasedSubscription(Inbox with { LifecycleNotificationUrl = Lifecycle }, null, Now, 0));

        await Poller().PollDueAsync(Ct);

        _sessions.ReceivedCalls().ShouldBeEmpty();
        await _endpoint.Received(1).DeliverLifecycleAsync(Lifecycle, Arg.Is<LifecycleNotification>(n => n.LifecycleEvent == "reauthorizationRequired"), Arg.Any<CancellationToken>());
        await _store.Received().CompletePollAsync("s1", null, null, null, Now.AddSeconds(20), 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unreachable_identity_provider_backs_off_without_asking_for_reauthorization()
    {
        Due(new LeasedSubscription(Inbox with { LifecycleNotificationUrl = Lifecycle }, "refresh-1", Now, 0));
        _sessions.RenewAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("keycloak down"));

        await Poller().PollDueAsync(Ct);

        _endpoint.ReceivedCalls().ShouldBeEmpty();
        await _store.Received().CompletePollAsync("s1", null, null, null, Now.AddSeconds(20), 1, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(4, 320)]
    [InlineData(8, 3600)]
    [InlineData(40, 3600)]
    public async Task The_backoff_doubles_with_each_failure_up_to_an_hour(int failures, int seconds)
    {
        Due(new LeasedSubscription(Inbox, null, Now, failures));

        await Poller().PollDueAsync(Ct);

        await _store.Received().CompletePollAsync("s1", null, null, null, Now.AddSeconds(seconds), failures + 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_lease_polls_nothing()
    {
        _store.LeaseDueAsync(default, default, default, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("db down"));

        await Poller().PollDueAsync(Ct);

        _sessions.ReceivedCalls().ShouldBeEmpty();
        await _store.DidNotReceiveWithAnyArgs().CompletePollAsync(default!, default, default, default, default, default, Ct);
    }

    [Fact]
    public async Task One_failing_subscription_does_not_hold_up_the_others()
    {
        Due(new LeasedSubscription(Inbox with { Id = "s0" }, null, Now, 0), new LeasedSubscription(Inbox, "refresh-1", Now, 0));
        _sessions.RenewAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(new SubscriberSession("access-1", "refresh-2"));
        _mail.DeltaAsync(Arg.Any<MailboxRef>(), Arg.Any<FolderRef>(), new DeltaToken("t0"), Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.Done([], new DeltaToken("t1")));

        await Poller().PollDueAsync(Ct);

        await _store.Received().CompletePollAsync("s0", null, null, null, Now.AddSeconds(20), 1, Arg.Any<CancellationToken>());
        await _store.Received().CompletePollAsync("s1", "t1", "refresh-2", Now, Now.AddSeconds(10), 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unconfigured_the_poller_does_nothing()
    {
        Due();
        var poller = new SubscriptionPoller(new ServiceCollection().AddSingleton(_store).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SubscriptionOptions()), new FakeTimeProvider(Now), NullLogger<SubscriptionPoller>.Instance);

        await poller.StartAsync(Ct);

        await poller.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), Ct); // it returns without starting its timer
        await poller.StopAsync(Ct);
        _store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_round_that_throws_is_logged_and_the_next_tick_polls_again()
    {
        var clock = new FakeTimeProvider(Now);
        var firstRound = new TaskCompletionSource();
        var secondRound = new TaskCompletionSource();
        var rounds = 0;
        _store.RemoveExpiredAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref rounds) == 1)
            {
                firstRound.TrySetResult();
                throw new InvalidOperationException("the store blew up");
            }

            secondRound.TrySetResult();
            return Result<IReadOnlyList<Subscription>>.Success([]);
        });
        _store.LeaseDueAsync(default, default, default, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<LeasedSubscription>>.Success([]));
        var poller = Poller(clock);

        await poller.StartAsync(Ct);
        await firstRound.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        clock.Advance(TimeSpan.FromSeconds(10));

        await secondRound.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        poller.ExecuteTask!.IsCompleted.ShouldBeFalse();
        await poller.StopAsync(Ct);
    }

    [Fact]
    public async Task An_ended_session_asks_for_reauthorization_once_on_the_lifecycle_url()
    {
        var subscription = Inbox with { LifecycleNotificationUrl = Lifecycle, ClientState = "c" };
        _sessions.RenewAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(Error.BackendAuthFailed("session ended"));

        Due(new LeasedSubscription(subscription, "refresh-1", Now, 0));
        await Poller().PollDueAsync(Ct);
        Due(new LeasedSubscription(subscription, "refresh-1", Now, 1));
        await Poller().PollDueAsync(Ct);

        await _endpoint.Received(1).DeliverLifecycleAsync(Lifecycle, Arg.Is<LifecycleNotification>(n => n.LifecycleEvent == "reauthorizationRequired" && n.ClientState == "c" && n.SubscriptionId == "s1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_expired_state_is_reported_missed_and_the_subscription_starts_again_from_now()
    {
        Due(new LeasedSubscription(Inbox with { LifecycleNotificationUrl = Lifecycle }, "refresh-1", Now, 0));
        _sessions.RenewAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(new SubscriberSession("access-1", "refresh-2"));
        _mail.DeltaAsync(Arg.Any<MailboxRef>(), Arg.Any<FolderRef>(), new DeltaToken("t0"), Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(Error.SyncStateGone("expired"));
        _mail.DeltaAsync(Arg.Any<MailboxRef>(), Arg.Any<FolderRef>(), null, Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.Done([new DeltaEntry<MailMessage>.Upsert(new MailMessage(new MessageId("m1")))], new DeltaToken("fresh")));

        await Poller().PollDueAsync(Ct);

        await _endpoint.Received(1).DeliverLifecycleAsync(Lifecycle, Arg.Is<LifecycleNotification>(n => n.LifecycleEvent == "missed"), Arg.Any<CancellationToken>());
        await _endpoint.DidNotReceiveWithAnyArgs().DeliverAsync(default!, default!, Ct);
        await _store.Received().CompletePollAsync("s1", "fresh", "refresh-2", Now, Now.AddSeconds(10), 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_recovery_backs_off_and_keeps_the_new_credential()
    {
        Due(new LeasedSubscription(Inbox, "refresh-1", Now, 0));
        _sessions.RenewAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(new SubscriberSession("access-1", "refresh-2"));
        _mail.DeltaAsync(Arg.Any<MailboxRef>(), Arg.Any<FolderRef>(), Arg.Any<DeltaToken?>(), Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(Error.SyncStateGone("expired"), Error.BackendUnavailable("down"));

        await Poller().PollDueAsync(Ct);

        await _store.Received().CompletePollAsync("s1", null, "refresh-2", null, Now.AddSeconds(20), 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Expired_subscriptions_are_swept_each_round()
    {
        Due();

        await Poller().PollDueAsync(Ct);

        await _store.Received().RemoveExpiredAsync(Now, Arg.Any<CancellationToken>());
    }
}
