using Nightware.Application.Common.Identity;
using Nightware.Application.Contacts;
using Nightware.Application.Mail;
using Nightware.Application.Subscriptions;
using Nightware.Application.Tasks;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Application.Tests.Subscriptions;

public sealed class SubscriptionsUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly Uri Hook = new("https://hooks.example.com/notify");
    private readonly ISubscriptionStore _store = Substitute.For<ISubscriptionStore>();
    private readonly ISubscriberCredentials _credentials = Substitute.For<ISubscriberCredentials>();
    private readonly INotificationEndpoint _endpoint = Substitute.For<INotificationEndpoint>();
    private readonly IUserContext _caller = Substitute.For<IUserContext>();
    private readonly IMailboxResolver _mailboxes = Substitute.For<IMailboxResolver>();
    private readonly TimeProvider _time = Substitute.For<TimeProvider>();
    private readonly IMailDeltaReader _mail = Substitute.For<IMailDeltaReader>();
    private readonly IContactFolderStore _folders = Substitute.For<IContactFolderStore>();
    private readonly IContactDelta _contacts = Substitute.For<IContactDelta>();
    private readonly ITaskDelta _tasks = Substitute.For<ITaskDelta>();
    private readonly IEventChangeFeed _events = Substitute.For<IEventChangeFeed>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SubscriptionsUseCaseTests()
    {
        _time.GetUtcNow().Returns(Now);
        _caller.Principal.Returns(new Principal("sub-1", "me", Me.Address, "Me"));
        _mailboxes.ResolveAsync(null, Arg.Any<CancellationToken>()).Returns(Me);
        _mailboxes.ResolveAsync("bob@example.com", Arg.Any<CancellationToken>()).Returns(new MailboxRef("bob@example.com"));
        _mailboxes.ResolveAsync("nobody", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));
        _endpoint.ValidateAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _credentials.IssueAsync(Arg.Any<CancellationToken>()).Returns("refresh");
        _store.CreateAsync(Arg.Any<Subscription>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Subscription>());
        _store.UpdateAsync(Arg.Any<Subscription>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Subscription>());
        _mail.DeltaAsync(Arg.Any<MailboxRef>(), Arg.Any<Nightware.Domain.Mail.FolderRef>(), null, Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<Nightware.Domain.Mail.MailMessage>.Done([], new DeltaToken("mail-baseline")));
        _folders.GetAsync(Arg.Any<MailboxRef>(), "contacts", Arg.Any<CancellationToken>()).Returns(new ContactFolder(new ContactFolderId("c"), "Contacts", null, "contacts"));
        _contacts.DeltaAsync(Arg.Any<MailboxRef>(), new ContactFolderId("c"), null, Arg.Any<CancellationToken>()).Returns(DeltaPage<Contact>.Done([], new DeltaToken("contacts-baseline")));
        _tasks.DeltaAsync(Arg.Any<MailboxRef>(), Arg.Any<TaskListId>(), null, Arg.Any<CancellationToken>())
            .Returns(DeltaPage<Nightware.Domain.Tasks.TodoTask>.Done([], new DeltaToken("tasks-baseline")));
        _events.BaselineAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>()).Returns(new FeedBaseline("events-baseline", []));
        _items.ReplaceAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    private readonly ISubscriptionItems _items = Substitute.For<ISubscriptionItems>();

    private SubscriptionsUseCase UseCase => new(_store, _credentials, _endpoint, new ChangeFeed(_mail, _folders, _contacts, _tasks, _events), _caller, _mailboxes, _time, _items);

    private static SubscriptionRequest Request(string resource = "me/mailFolders('Inbox')/messages", string changeType = "created,updated", TimeSpan? lifetime = null) =>
        new(resource, changeType, Hook.AbsoluteUri, Now + (lifetime ?? TimeSpan.FromHours(2))) { ClientState = "state", ApplicationId = "app" };

    [Theory]
    [InlineData("me/mailFolders('Inbox')/messages", SubscriptionResourceKind.Messages, "Inbox", "me@example.com")]
    [InlineData("me/mailFolders/AAMk=/messages", SubscriptionResourceKind.Messages, "AAMk=", "me@example.com")]
    [InlineData("users/bob@example.com/events", SubscriptionResourceKind.Events, null, "bob@example.com")]
    [InlineData("me/contacts", SubscriptionResourceKind.Contacts, null, "me@example.com")]
    [InlineData("me/todo/lists/L1/tasks", SubscriptionResourceKind.Tasks, "L1", "me@example.com")]
    public async Task Supported_resources_are_parsed(string resource, SubscriptionResourceKind kind, string? folder, string mailbox)
    {
        var created = (await UseCase.CreateAsync(Request(resource), Ct)).Value;

        created.Target.ShouldBe(new SubscriptionTarget(new MailboxRef(mailbox), kind, folder));
        created.Resource.ShouldBe(resource);
        created.CreatorId.ShouldBe("sub-1");
        created.ChangeType.ShouldBe(SubscriptionChangeType.Created | SubscriptionChangeType.Updated);
        created.ApplicationId.ShouldBe("app");
        created.SyncState.ShouldEndWith("-baseline");
        await _store.Received().CreateAsync(Arg.Any<Subscription>(), "refresh", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("me/drive/root")]
    [InlineData("me/messages/AAMk=")]
    [InlineData("")]
    public async Task Other_resources_are_400(string resource) =>
        (await UseCase.CreateAsync(Request(resource), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);

    [Fact]
    public async Task Every_message_in_the_mailbox_is_not_offered() =>
        (await UseCase.CreateAsync(Request("me/messages"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);

    [Fact]
    public async Task A_folder_that_cannot_be_read_stores_nothing()
    {
        _mail.DeltaAsync(Arg.Any<MailboxRef>(), Arg.Any<Nightware.Domain.Mail.FolderRef>(), null, Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(Error.NotFound("no such folder"));

        (await UseCase.CreateAsync(Request(), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        await _store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task An_unknown_user_is_their_resolvers_error() =>
        (await UseCase.CreateAsync(Request("users/nobody/events"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

    [Theory]
    [InlineData("")]
    [InlineData("created,moved")]
    [InlineData("none")]
    public async Task Change_types_must_be_graphs(string changeType) =>
        (await UseCase.CreateAsync(Request(changeType: changeType), Ct)).Error!.Target.ShouldBe("changeType");

    [Fact]
    public async Task Expiry_is_raised_to_45_minutes_and_capped_per_resource()
    {
        (await UseCase.CreateAsync(Request(lifetime: TimeSpan.FromMinutes(5)), Ct)).Value.ExpirationDateTime.ShouldBe(Now.AddMinutes(45));
        (await UseCase.CreateAsync(Request(lifetime: TimeSpan.FromMinutes(10080)), Ct)).IsSuccess.ShouldBeTrue();
        (await UseCase.CreateAsync(Request(lifetime: TimeSpan.FromMinutes(10081)), Ct)).Error!.Target.ShouldBe("expirationDateTime");
        (await UseCase.CreateAsync(Request("me/todo/lists/L1/tasks", lifetime: TimeSpan.FromMinutes(4231)), Ct)).Error!.Target.ShouldBe("expirationDateTime");
        (await UseCase.CreateAsync(Request() with { ExpirationDateTime = null }, Ct)).Error!.Target.ShouldBe("expirationDateTime");
    }

    [Fact]
    public async Task Rich_notifications_long_client_state_and_bad_urls_are_400()
    {
        (await UseCase.CreateAsync(Request() with { IncludeResourceData = true }, Ct)).Error!.Target.ShouldBe("includeResourceData");
        (await UseCase.CreateAsync(Request() with { ClientState = new string('x', 129) }, Ct)).Error!.Target.ShouldBe("clientState");
        (await UseCase.CreateAsync(Request() with { NotificationUrl = "not a url" }, Ct)).Error!.Target.ShouldBe("notificationUrl");
        (await UseCase.CreateAsync(Request() with { LifecycleNotificationUrl = "ftp://x" }, Ct)).Error!.Target.ShouldBe("lifecycleNotificationUrl");
        _store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_handshake_stores_nothing_and_issues_no_credential()
    {
        _endpoint.ValidateAsync(Hook, Arg.Any<CancellationToken>()).Returns(Error.Validation("failed", "notificationUrl"));

        (await UseCase.CreateAsync(Request(), Ct)).Error!.Target.ShouldBe("notificationUrl");
        _credentials.ReceivedCalls().ShouldBeEmpty();
        _store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_lifecycle_url_is_validated_too()
    {
        var lifecycle = new Uri("https://hooks.example.com/lifecycle");
        _endpoint.ValidateAsync(lifecycle, Arg.Any<CancellationToken>()).Returns(Error.Validation("failed", "notificationUrl"));

        (await UseCase.CreateAsync(Request() with { LifecycleNotificationUrl = lifecycle.AbsoluteUri }, Ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Renewal_extends_the_expiry_and_reissues_the_credential()
    {
        var existing = (await UseCase.CreateAsync(Request(), Ct)).Value;
        _store.GetAsync("sub-1", existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        _store.GetAsync("sub-1", "nope", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));
        _credentials.IssueAsync(Arg.Any<CancellationToken>()).Returns("renewed");

        var renewed = (await UseCase.UpdateAsync(existing.Id, new SubscriptionPatch(Now.AddDays(3), null), Ct)).Value;

        renewed.ExpirationDateTime.ShouldBe(Now.AddDays(3));
        await _store.Received().UpdateAsync(Arg.Any<Subscription>(), "renewed", Arg.Any<CancellationToken>());
        (await UseCase.UpdateAsync(existing.Id, new SubscriptionPatch(Now.AddDays(8), null), Ct)).Error!.Target.ShouldBe("expirationDateTime");
        (await UseCase.UpdateAsync("nope", new SubscriptionPatch(Now.AddDays(1), null), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_new_notification_url_is_validated_before_it_is_kept()
    {
        var existing = (await UseCase.CreateAsync(Request(), Ct)).Value;
        _store.GetAsync("sub-1", existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        _endpoint.ValidateAsync(new Uri("https://other.example.com/n"), Arg.Any<CancellationToken>()).Returns(Error.Validation("failed", "notificationUrl"));

        (await UseCase.UpdateAsync(existing.Id, new SubscriptionPatch(null, "https://other.example.com/n"), Ct)).IsSuccess.ShouldBeFalse();
        await _store.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, Ct);
    }

    [Fact]
    public async Task Reads_and_deletes_are_the_callers_own()
    {
        _store.ListAsync("sub-1", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Subscription>>.Success([]));
        _store.DeleteAsync("sub-1", "x", Arg.Any<CancellationToken>()).Returns(Result.Success());

        (await UseCase.ListAsync(Ct)).Value.ShouldBeEmpty();
        (await UseCase.DeleteAsync("x", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_new_subscription_starts_out_knowing_what_exists()
    {
        _mail.DeltaAsync(Arg.Any<MailboxRef>(), Arg.Any<Nightware.Domain.Mail.FolderRef>(), null, Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<Nightware.Domain.Mail.MailMessage>.Done(
                [new DeltaEntry<Nightware.Domain.Mail.MailMessage>.Upsert(new Nightware.Domain.Mail.MailMessage(new Nightware.Domain.Kernel.MessageId("m1")))],
                new DeltaToken("mail-baseline")));

        var created = (await UseCase.CreateAsync(Request(), Ct)).Value;

        await _items.Received(1).ReplaceAsync(created.Id, Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "m1" })), Arg.Any<CancellationToken>());
    }
}
