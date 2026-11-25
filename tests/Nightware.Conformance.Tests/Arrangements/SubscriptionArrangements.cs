using Microsoft.Extensions.DependencyInjection;

using Nightware.Application.Contacts;
using Nightware.Application.Mail;
using Nightware.Application.Subscriptions;
using Nightware.Application.Tasks;
using Nightware.Domain.Contacts;
using Nightware.Domain.Mail;
using Nightware.Domain.Tasks;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 16 change-notification subscriptions (ADR 0006 D10), against captures from Microsoft 365.</summary>
public sealed class SubscriptionArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly DateTimeOffset Captured = new(2026, 11, 20, 22, 40, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expiry = new(2026, 11, 20, 23, 40, 0, TimeSpan.Zero);
    private const string Creator = "22222222-2222-2222-2222-222222222222";
    private const string Application = "00000000-0000-0000-0000-000000000001";

    private static Subscription Inbox => new(
        "00000000-0000-0000-0000-000000000001", Creator, "me/mailFolders('Inbox')/messages",
        new SubscriptionTarget(Me, SubscriptionResourceKind.Messages, "Inbox"),
        SubscriptionChangeType.Created | SubscriptionChangeType.Updated | SubscriptionChangeType.Deleted,
        new Uri("https://webhook.example.com/notify"), Expiry)
    {
        ClientState = "nightware-capture-state",
        ApplicationId = Application,
    };

    private static void Services(IServiceCollection services) => services.AddSingleton<TimeProvider>(new FixedTime(Captured));

    /// <summary>A create the endpoint validates and the store keeps under the id Graph gave it.</summary>
    private static ConformanceArrangement Create(string operation, string id, SubscriptionTarget target) =>
        new(operation, fakes =>
        {
            fakes.For<INotificationEndpoint>().ValidateAsync(Arg.Is<Uri>(u => u.Host == "webhook.example.com"), Arg.Any<CancellationToken>()).Returns(Result.Success());
            fakes.For<ISubscriberCredentials>().IssueAsync(Arg.Any<CancellationToken>()).Returns("refresh");
            Baselines(fakes);
            fakes.For<ISubscriptionStore>()
                .CreateAsync(Arg.Is<Subscription>(s => s.Target == target && s.ExpirationDateTime == Expiry && s.ClientState == "nightware-capture-state"), "refresh", Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<Subscription>() with { Id = id, CreatorId = Creator, ApplicationId = Application });
        })
        {
            Services = Services,
        };

    /// <summary>Each resource's change feed answers "nothing yet" with a baseline state.</summary>
    private static void Baselines(PortFakes fakes)
    {
        fakes.For<IMailDeltaReader>().DeltaAsync(Me, Arg.Any<FolderRef>(), null, Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.Done([], new DeltaToken("mail")));
        fakes.For<IContactFolderStore>().GetAsync(Me, "contacts", Arg.Any<CancellationToken>()).Returns(new ContactFolder(new ContactFolderId("c"), "Contacts", null, "contacts"));
        fakes.For<IContactDelta>().DeltaAsync(Me, new ContactFolderId("c"), null, Arg.Any<CancellationToken>()).Returns(DeltaPage<Contact>.Done([], new DeltaToken("contacts")));
        fakes.For<ITaskDelta>().DeltaAsync(Me, Arg.Any<TaskListId>(), null, Arg.Any<CancellationToken>()).Returns(DeltaPage<TodoTask>.Done([], new DeltaToken("tasks")));
        fakes.For<IEventChangeFeed>().BaselineAsync(Me, Arg.Any<CancellationToken>()).Returns(new FeedBaseline("events", []));
        fakes.For<ITeamsChangeFeed>().StartAsync(Arg.Is<SubscriptionTarget>(t => t.Mailbox == Me), Arg.Any<CancellationToken>()).Returns("teams");
        fakes.For<ISubscriptionItems>().ReplaceAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return Create("subscriptions.create", "00000000-0000-0000-0000-000000000001", new SubscriptionTarget(Me, SubscriptionResourceKind.Messages, "Inbox"));
        yield return Create("subscriptions.create-events", "00000000-0000-0000-0000-000000000003", new SubscriptionTarget(Me, SubscriptionResourceKind.Events, null));
        yield return Create("subscriptions.create-contacts", "00000000-0000-0000-0000-000000000004", new SubscriptionTarget(Me, SubscriptionResourceKind.Contacts, null));
        yield return Create("subscriptions.create-tasks", "00000000-0000-0000-0000-000000000005", new SubscriptionTarget(Me, SubscriptionResourceKind.Tasks, "AAMk-ref-0001="));

        // Phase 29: Teams resources (ADR 0009 D11).
        yield return Create("subscriptions.create-chat-messages", "00000000-0000-0000-0000-000000000006", new SubscriptionTarget(Me, SubscriptionResourceKind.ChatMessages, "nge9dkks"));
        yield return Create("subscriptions.create-channel-messages", "00000000-0000-0000-0000-000000000007",
            new SubscriptionTarget(Me, SubscriptionResourceKind.ChannelMessages, "6LOrxifQ5p4vRt2I3iplImWqdpjlADT/xtugjbpy"));
        yield return Create("subscriptions.create-chats", "00000000-0000-0000-0000-000000000008", new SubscriptionTarget(Me, SubscriptionResourceKind.Chats, null));
        yield return Create("subscriptions.create-chat-members", "00000000-0000-0000-0000-000000000009", new SubscriptionTarget(Me, SubscriptionResourceKind.ChatMembers, "nge9dkks"));

        yield return new ConformanceArrangement("subscriptions.list", static fakes =>
            fakes.For<ISubscriptionStore>().ListAsync("conformance-user", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Subscription>>.Success([Inbox])));

        yield return new ConformanceArrangement("subscriptions.get", static fakes =>
            fakes.For<ISubscriptionStore>().GetAsync("conformance-user", Inbox.Id, Arg.Any<CancellationToken>()).Returns(Inbox));

        yield return new ConformanceArrangement("subscriptions.update", static fakes =>
        {
            fakes.For<ISubscriptionStore>().GetAsync("conformance-user", Inbox.Id, Arg.Any<CancellationToken>()).Returns(Inbox);
            fakes.For<ISubscriberCredentials>().IssueAsync(Arg.Any<CancellationToken>()).Returns("renewed");
            fakes.For<ISubscriptionStore>()
                .UpdateAsync(Arg.Is<Subscription>(s => s.ExpirationDateTime == Expiry.AddHours(1)), "renewed", Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<Subscription>());
        })
        {
            Services = Services,
        };

        yield return new ConformanceArrangement("subscriptions.delete", static fakes =>
            fakes.For<ISubscriptionStore>().DeleteAsync("conformance-user", Inbox.Id, Arg.Any<CancellationToken>()).Returns(Result.Success()));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
