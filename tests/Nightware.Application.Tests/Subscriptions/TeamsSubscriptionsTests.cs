using Nightware.Application.Common.Identity;
using Nightware.Application.Contacts;
using Nightware.Application.Mail;
using Nightware.Application.Subscriptions;
using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Application.Tests.Subscriptions;

/// <summary>Change notifications for Teams resources (ADR 0009 D11): parsing, access, lifetimes and the feed's routing.</summary>
public sealed class TeamsSubscriptionsTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
    private const string Hook = "https://hooks.example.com/notify";
    private readonly ISubscriptionStore _store = Substitute.For<ISubscriptionStore>();
    private readonly ISubscriberCredentials _credentials = Substitute.For<ISubscriberCredentials>();
    private readonly INotificationEndpoint _endpoint = Substitute.For<INotificationEndpoint>();
    private readonly IUserContext _caller = Substitute.For<IUserContext>();
    private readonly IMailboxResolver _mailboxes = Substitute.For<IMailboxResolver>();
    private readonly TimeProvider _time = Substitute.For<TimeProvider>();
    private readonly ITeamsChangeFeed _teams = Substitute.For<ITeamsChangeFeed>();
    private readonly ISubscriptionItems _items = Substitute.For<ISubscriptionItems>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TeamsSubscriptionsTests()
    {
        _time.GetUtcNow().Returns(Now);
        _caller.Principal.Returns(new Principal("sub-1", "me", Me.Address, "Me"));
        _mailboxes.ResolveAsync(null, Arg.Any<CancellationToken>()).Returns(Me);
        _mailboxes.ResolveAsync("me@example.com", Arg.Any<CancellationToken>()).Returns(Me);
        _mailboxes.ResolveAsync("id-me", Arg.Any<CancellationToken>()).Returns(Me);
        _mailboxes.ResolveAsync("shared@example.com", Arg.Any<CancellationToken>()).Returns(new MailboxRef("shared@example.com"));
        _endpoint.ValidateAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _credentials.IssueAsync(Arg.Any<CancellationToken>()).Returns("refresh");
        _store.CreateAsync(Arg.Any<Subscription>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Subscription>());
        _store.UpdateAsync(Arg.Any<Subscription>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Subscription>());
        _teams.StartAsync(Arg.Any<SubscriptionTarget>(), Arg.Any<CancellationToken>()).Returns("teams-start");
        _items.ReplaceAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    private static ChangeFeed Feed(ITeamsChangeFeed? teams) => new(
        Substitute.For<IMailDeltaReader>(), Substitute.For<IContactFolderStore>(), Substitute.For<IContactDelta>(), Substitute.For<ITaskDelta>(), Substitute.For<IEventChangeFeed>(), teams);

    private SubscriptionsUseCase UseCase(ITeamsChangeFeed? teams = null) => new(_store, _credentials, _endpoint, Feed(teams ?? _teams), _caller, _mailboxes, _time, _items);

    private static SubscriptionRequest Request(string resource, TimeSpan? lifetime = null, string? lifecycle = null) =>
        new(resource, "created,updated,deleted", Hook, Now + (lifetime ?? TimeSpan.FromMinutes(50))) { LifecycleNotificationUrl = lifecycle };

    [Theory]
    [InlineData("chats/19abc/messages", SubscriptionResourceKind.ChatMessages, "19abc")]
    [InlineData("/chats('19abc')/messages", SubscriptionResourceKind.ChatMessages, "19abc")]
    [InlineData("me/chats/getAllMessages", SubscriptionResourceKind.AllChatMessages, null)]
    [InlineData("users/id-me/chats/getAllMessages", SubscriptionResourceKind.AllChatMessages, null)]
    [InlineData("teams/T1/channels/c1/messages", SubscriptionResourceKind.ChannelMessages, "T1/c1")]
    [InlineData("teams('T1')/channels('c1')/messages", SubscriptionResourceKind.ChannelMessages, "T1/c1")]
    [InlineData("me/chats", SubscriptionResourceKind.Chats, null)]
    [InlineData("users/me@example.com/chats", SubscriptionResourceKind.Chats, null)]
    [InlineData("chats/19abc", SubscriptionResourceKind.Chat, "19abc")]
    [InlineData("chats('19abc')/members", SubscriptionResourceKind.ChatMembers, "19abc")]
    public async Task Teams_resources_are_the_callers_and_start_now(string resource, SubscriptionResourceKind kind, string? folder)
    {
        var created = await UseCase().CreateAsync(Request(resource), Ct);

        created.Value.Target.ShouldBe(new SubscriptionTarget(Me, kind, folder));
        created.Value.SyncState.ShouldBe("teams-start");
        await _items.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Someone_elses_chats_are_forbidden()
    {
        var result = await UseCase().CreateAsync(Request("users/shared@example.com/chats/getAllMessages"), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Theory]
    [InlineData("chats/19abc/messages?$filter=mentions/any(u: u/mentioned/user/id eq 'x')")]
    [InlineData("me/chats?notifyOnUserSpecificProperties=true")]
    [InlineData("chats/getAllMessages")]
    [InlineData("teams/getAllMessages")]
    [InlineData("chats")]
    public async Task Query_options_and_tenant_wide_resources_are_refused(string resource)
    {
        var result = await UseCase().CreateAsync(Request(resource), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Target.ShouldBe("resource");
    }

    [Fact]
    public async Task Teams_subscriptions_live_three_days_and_past_an_hour_need_a_lifecycle_url()
    {
        (await UseCase().CreateAsync(Request("me/chats", TimeSpan.FromMinutes(4321), "https://hooks.example.com/life"), Ct)).Error!.Target.ShouldBe("expirationDateTime");
        (await UseCase().CreateAsync(Request("me/chats", TimeSpan.FromMinutes(61)), Ct)).Error!.Target.ShouldBe("lifecycleNotificationUrl");
        (await UseCase().CreateAsync(Request("me/chats", TimeSpan.FromMinutes(4320), "https://hooks.example.com/life"), Ct)).IsSuccess.ShouldBeTrue();
        (await UseCase().CreateAsync(Request("me/chats", TimeSpan.FromMinutes(60)), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Renewing_past_an_hour_without_a_lifecycle_url_is_refused()
    {
        var existing = new Subscription("s1", "sub-1", "me/chats", new SubscriptionTarget(Me, SubscriptionResourceKind.Chats, null),
            SubscriptionChangeType.Created, new Uri(Hook), Now + TimeSpan.FromMinutes(50));
        _store.GetAsync("sub-1", "s1", Arg.Any<CancellationToken>()).Returns(existing);

        var refused = await UseCase().UpdateAsync("s1", new SubscriptionPatch(Now + TimeSpan.FromHours(2), null), Ct);
        var renewed = await UseCase().UpdateAsync("s1", new SubscriptionPatch(Now + TimeSpan.FromMinutes(55), null), Ct);

        refused.Error!.Target.ShouldBe("lifecycleNotificationUrl");
        renewed.Value.ExpirationDateTime.ShouldBe(Now + TimeSpan.FromMinutes(55));
    }

    [Fact]
    public async Task Without_talk_teams_resources_are_unsupported()
    {
        var created = await new SubscriptionsUseCase(_store, _credentials, _endpoint, Feed(null), _caller, _mailboxes, _time, _items).CreateAsync(Request("me/chats"), Ct);
        var target = new SubscriptionTarget(Me, SubscriptionResourceKind.Chats, null);

        created.Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        (await Feed(null).ChangesAsync(target, "s", Now, Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
    }

    [Fact]
    public async Task Changes_come_from_the_teams_feed()
    {
        var target = new SubscriptionTarget(Me, SubscriptionResourceKind.ChatMessages, "19abc");
        var batch = new ChangeBatch([new ResourceChange("5", SubscriptionChangeType.Created)], "next", Classified: true);
        _teams.ChangesAsync(target, "s", Arg.Any<CancellationToken>()).Returns(batch);

        (await Feed(_teams).ChangesAsync(target, "s", Now, Ct)).Value.ShouldBe(batch);
    }
}
