using Nightware.Application.Contacts;
using Nightware.Application.Mail;
using Nightware.Application.Subscriptions;
using Nightware.Application.Tasks;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.Subscriptions;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tests.Subscriptions;

public sealed class SubscriptionNotifierTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly DateTimeOffset LastPoll = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
    private readonly IMailDeltaReader _mail = Substitute.For<IMailDeltaReader>();
    private readonly IContactFolderStore _folders = Substitute.For<IContactFolderStore>();
    private readonly IContactDelta _contacts = Substitute.For<IContactDelta>();
    private readonly ITaskDelta _tasks = Substitute.For<ITaskDelta>();
    private readonly IEventChangeFeed _events = Substitute.For<IEventChangeFeed>();
    private readonly INotificationEndpoint _endpoint = Substitute.For<INotificationEndpoint>();
    private readonly ISubscriptionItems _items = Substitute.For<ISubscriptionItems>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SubscriptionNotifierTests()
    {
        _endpoint.DeliverAsync(Arg.Any<Uri>(), Arg.Any<IReadOnlyList<ChangeNotification>>(), Arg.Any<CancellationToken>()).Returns(Result.Success());

        // Untracked unless a test says otherwise: the feed's own guess stands.
        _items.KnownAsync(default!, default!, default).ReturnsForAnyArgs(Result<IReadOnlySet<string>?>.Success(null));
        _items.ReplaceAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        _items.RecordAsync(default!, default!, default!, default).ReturnsForAnyArgs(Result.Success());
    }

    private ChangeFeed Feed => new(_mail, _folders, _contacts, _tasks, _events);

    private SubscriptionNotifier Notifier => new(Feed, _endpoint, _items);

    private static Subscription Inbox(SubscriptionChangeType types = SubscriptionChangeType.Created | SubscriptionChangeType.Updated | SubscriptionChangeType.Deleted) => new(
        "s1", "sub", "me/mailFolders('Inbox')/messages", new SubscriptionTarget(Me, SubscriptionResourceKind.Messages, "Inbox"), types,
        new Uri("https://hooks.example.com/n"), LastPoll.AddDays(1)) { ClientState = "state", SyncState = "t0" };

    private static MailMessage Message(string id, DateTimeOffset created) => new(new MessageId(id)) { CreatedDateTime = created };

    private void MailPages()
    {
        _mail.DeltaAsync(Me, new FolderRef.WellKnown(WellKnownFolder.Inbox), new DeltaToken("t0"), Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.More([new DeltaEntry<MailMessage>.Upsert(Message("new", LastPoll.AddSeconds(5)))], new DeltaToken("p2")));
        _mail.DeltaAsync(Me, new FolderRef.WellKnown(WellKnownFolder.Inbox), new DeltaToken("p2"), Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.Done(
                [new DeltaEntry<MailMessage>.Upsert(Message("old", LastPoll.AddDays(-1))), new DeltaEntry<MailMessage>.Removed("gone", RemovedReason.Deleted)],
                new DeltaToken("t1")));
    }

    [Fact]
    public async Task Changes_since_the_state_are_classified_and_delivered_then_the_state_advances()
    {
        MailPages();

        var outcome = (await Notifier.PollAsync(Inbox(), LastPoll, Ct)).Value;

        outcome.ShouldBe(new PollOutcome("t1", 3));
        await _endpoint.Received().DeliverAsync(new Uri("https://hooks.example.com/n"), Arg.Is<IReadOnlyList<ChangeNotification>>(n =>
            n.Select(x => x.Id + ":" + x.ChangeType).SequenceEqual(new[] { "new:created", "old:updated", "gone:deleted" })
            && n.All(x => x.ClientState == "state" && x.SubscriptionId == "s1")
            && n[0].Resource == "Users/me@example.com/Messages/new" && n[0].ODataType == "#Microsoft.Graph.Message"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_the_subscribed_change_types_are_sent()
    {
        MailPages();

        (await Notifier.PollAsync(Inbox(SubscriptionChangeType.Created), LastPoll, Ct)).Value.Delivered.ShouldBe(1);
    }

    [Fact]
    public async Task Nothing_to_send_needs_no_delivery_but_still_advances()
    {
        _mail.DeltaAsync(Me, Arg.Any<FolderRef>(), new DeltaToken("t0"), Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.Done([], new DeltaToken("t1")));

        (await Notifier.PollAsync(Inbox(), LastPoll, Ct)).Value.ShouldBe(new PollOutcome("t1", 0));
        _endpoint.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_delivery_does_not_advance_the_state()
    {
        MailPages();
        _endpoint.DeliverAsync(Arg.Any<Uri>(), Arg.Any<IReadOnlyList<ChangeNotification>>(), Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        (await Notifier.PollAsync(Inbox(), LastPoll, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Without_a_state_the_poll_takes_a_baseline_and_sends_nothing()
    {
        _mail.DeltaAsync(Me, Arg.Any<FolderRef>(), null, Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.Done([new DeltaEntry<MailMessage>.Upsert(Message("existing", LastPoll))], new DeltaToken("b")));

        (await Notifier.PollAsync(Inbox() with { SyncState = null }, LastPoll, Ct)).Value.ShouldBe(new PollOutcome("b", 0));
        _endpoint.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Contacts_follow_the_default_folder_tasks_their_list_and_events_their_own_feed()
    {
        _folders.GetAsync(Me, "contacts", Arg.Any<CancellationToken>()).Returns(new ContactFolder(new ContactFolderId("c"), "Contacts", null, "contacts"));
        _contacts.DeltaAsync(Me, new ContactFolderId("c"), new DeltaToken("s"), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<Contact>.Done([new DeltaEntry<Contact>.Removed("pat", RemovedReason.Deleted)], new DeltaToken("s2")));
        _tasks.DeltaAsync(Me, new TaskListId("L"), new DeltaToken("s"), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<TodoTask>.Done([new DeltaEntry<TodoTask>.Upsert(new TodoTask(new TaskId("t"), "x") { CreatedDateTime = LastPoll.AddMinutes(1) })], new DeltaToken("s3")));
        _events.ChangesAsync(Me, "s", LastPoll, Arg.Any<CancellationToken>()).Returns(new ChangeBatch([new ResourceChange("e", SubscriptionChangeType.Updated)], "s4"));

        (await Feed.ChangesAsync(new SubscriptionTarget(Me, SubscriptionResourceKind.Contacts, null), "s", LastPoll, Ct)).Value
            .ShouldBe(new ChangeBatch([new ResourceChange("pat", SubscriptionChangeType.Deleted)], "s2"), new ChangeBatchComparer());
        (await Feed.ChangesAsync(new SubscriptionTarget(Me, SubscriptionResourceKind.Tasks, "L"), "s", LastPoll, Ct)).Value.Changes
            .ShouldBe([new ResourceChange("t", SubscriptionChangeType.Created)]);
        (await Feed.ChangesAsync(new SubscriptionTarget(Me, SubscriptionResourceKind.Events, null), "s", LastPoll, Ct)).Value.State.ShouldBe("s4");
    }

    [Fact]
    public void Notifications_name_resources_as_graph_does()
    {
        var change = new ResourceChange("x", SubscriptionChangeType.Created);
        string Path(SubscriptionResourceKind kind, string? folder = null) =>
            SubscriptionNotifier.ToNotification(Inbox() with { Target = new SubscriptionTarget(Me, kind, folder) }, change).Resource;

        Path(SubscriptionResourceKind.Events).ShouldBe("Users/me@example.com/Events/x");
        Path(SubscriptionResourceKind.Contacts).ShouldBe("Users/me@example.com/Contacts/x");
        Path(SubscriptionResourceKind.Tasks, "L").ShouldBe("Users/me@example.com/todo/lists/L/tasks/x");
    }

    [Fact]
    public async Task An_item_stamped_in_the_same_second_as_the_last_poll_is_still_new()
    {
        _mail.DeltaAsync(Me, Arg.Any<FolderRef>(), new DeltaToken("t0"), Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.Done(
                [new DeltaEntry<MailMessage>.Upsert(Message("truncated", LastPoll.AddSeconds(-1))), new DeltaEntry<MailMessage>.Upsert(Message("older", LastPoll.AddSeconds(-3)))],
                new DeltaToken("t1")));

        var changes = (await Feed.ChangesAsync(new SubscriptionTarget(Me, SubscriptionResourceKind.Messages, "Inbox"), "t0", LastPoll, Ct)).Value.Changes;

        changes.ShouldBe([new ResourceChange("truncated", SubscriptionChangeType.Created), new ResourceChange("older", SubscriptionChangeType.Updated)]);
    }

    [Fact]
    public void Well_known_folder_names_are_told_from_ids()
    {
        ChangeFeed.MailFolder("inbox").ShouldBe(new FolderRef.WellKnown(WellKnownFolder.Inbox));
        ChangeFeed.MailFolder("AAMk=").ShouldBe(new FolderRef.ById(new FolderId("AAMk=")));
    }

    private sealed class ChangeBatchComparer : IEqualityComparer<ChangeBatch>
    {
        public bool Equals(ChangeBatch? x, ChangeBatch? y) => x!.State == y!.State && x.Changes.SequenceEqual(y.Changes);

        public int GetHashCode(ChangeBatch obj) => obj.State.GetHashCode(StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tracked_subscription_classifies_by_what_it_has_seen_not_by_dates()
    {
        // The dates say "new" is new and "old" is old; what the subscription has seen says otherwise.
        MailPages();
        _items.KnownAsync("s1", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlySet<string>?>.Success(new HashSet<string> { "new" }));

        (await Notifier.PollAsync(Inbox(), LastPoll, Ct)).IsSuccess.ShouldBeTrue();

        await _endpoint.Received().DeliverAsync(Arg.Any<Uri>(), Arg.Is<IReadOnlyList<ChangeNotification>>(n =>
            n.Select(x => x.Id + ":" + x.ChangeType).SequenceEqual(new[] { "new:updated", "old:created", "gone:deleted" })), Arg.Any<CancellationToken>());
        await _items.Received(1).RecordAsync("s1",
            Arg.Is<IReadOnlyCollection<string>>(a => a.SequenceEqual(new[] { "old" })),
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { "gone" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task What_was_seen_advances_only_with_a_delivered_batch()
    {
        MailPages();
        _items.KnownAsync("s1", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlySet<string>?>.Success(new HashSet<string>()));
        _endpoint.DeliverAsync(Arg.Any<Uri>(), Arg.Any<IReadOnlyList<ChangeNotification>>(), Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        (await Notifier.PollAsync(Inbox(), LastPoll, Ct)).IsSuccess.ShouldBeFalse();

        await _items.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Starting_over_seeds_what_exists_now()
    {
        _mail.DeltaAsync(Me, new FolderRef.WellKnown(WellKnownFolder.Inbox), null, Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.Done([new DeltaEntry<MailMessage>.Upsert(Message("a", LastPoll)), new DeltaEntry<MailMessage>.Upsert(Message("b", LastPoll))], new DeltaToken("t9")));

        (await Notifier.PollAsync(Inbox() with { SyncState = null }, LastPoll, Ct)).Value.ShouldBe(new PollOutcome("t9", 0));

        await _items.Received(1).ReplaceAsync("s1", Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "a", "b" })), Arg.Any<CancellationToken>());
    }
}
