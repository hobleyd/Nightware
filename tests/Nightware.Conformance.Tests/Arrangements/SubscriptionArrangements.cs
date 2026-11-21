using Microsoft.Extensions.DependencyInjection;

using Nightware.Application.Subscriptions;
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
            fakes.For<INotificationEndpoint>().ValidateAsync(new Uri("https://webhook.example.com/notify"), Arg.Any<CancellationToken>()).Returns(Result.Success());
            fakes.For<ISubscriberCredentials>().IssueAsync(Arg.Any<CancellationToken>()).Returns("refresh");
            fakes.For<ISubscriptionStore>()
                .CreateAsync(Arg.Is<Subscription>(s => s.Target == target && s.ExpirationDateTime == Expiry && s.ClientState == "nightware-capture-state"), "refresh", Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<Subscription>() with { Id = id, CreatorId = Creator, ApplicationId = Application });
        })
        {
            Services = Services,
        };

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return Create("subscriptions.create", "00000000-0000-0000-0000-000000000001", new SubscriptionTarget(Me, SubscriptionResourceKind.Messages, "Inbox"));
        yield return Create("subscriptions.create-events", "00000000-0000-0000-0000-000000000003", new SubscriptionTarget(Me, SubscriptionResourceKind.Events, null));
        yield return Create("subscriptions.create-contacts", "00000000-0000-0000-0000-000000000004", new SubscriptionTarget(Me, SubscriptionResourceKind.Contacts, null));
        yield return Create("subscriptions.create-tasks", "00000000-0000-0000-0000-000000000005", new SubscriptionTarget(Me, SubscriptionResourceKind.Tasks, "AAMk-ref-0001="));

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
