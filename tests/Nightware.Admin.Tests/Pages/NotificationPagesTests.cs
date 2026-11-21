using System.Net;

using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration.Subscriptions;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Admin.Tests.Pages;

public sealed class NotificationPagesTests : IAsyncDisposable
{
    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static ManagedSubscription Inbox(string id, string creator, int failures = 0) => new(
        new Subscription(id, creator, "me/mailFolders('Inbox')/messages", new SubscriptionTarget(new MailboxRef("dan@acme.example"), SubscriptionResourceKind.Messages, "Inbox"),
            SubscriptionChangeType.Created, new Uri("https://hooks.app.example/notify"), new DateTimeOffset(2026, 11, 30, 9, 0, 0, TimeSpan.Zero)) { ApplicationId = "mail-app" },
        null, failures, null);

    [Fact]
    public async Task A_users_page_lists_their_subscriptions_and_removes_one()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");
        _host.Subscriptions.ListAsync("u-dan", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<ManagedSubscription>>.Success([Inbox("s1", "u-dan", failures: 2)]));
        _host.Subscriptions.RemoveAsync("s1", Arg.Any<CancellationToken>()).Returns(Inbox("s1", "u-dan").Subscription);
        HttpClient client = _host.ClientAs(ConsoleHost.EntityAdmin);

        string html = await client.GetStringAsync("/users/u-dan", Ct);
        html.ShouldContain("Apps notified of changes");
        html.ShouldContain("Mail in Inbox of dan@acme.example");
        html.ShouldContain("hooks.app.example");
        html.ShouldContain("Failing (2 in a row)");

        using HttpResponseMessage done = await ConsoleHost.PostFormAsync(client, "/users/u-dan", "/users/u-dan?handler=RemoveSubscription", new Dictionary<string, string> { ["subscriptionId"] = "s1" });
        done.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.Subscriptions.Received(1).RemoveAsync("s1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_notifications_page_is_for_global_admins_and_lists_every_subscription()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");
        _host.Subscriptions.ListAsync(null, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<ManagedSubscription>>.Success([Inbox("s1", "u-dan")]));

        using HttpResponseMessage denied = await _host.ClientAs(ConsoleHost.EntityAdmin).GetAsync("/notifications", Ct);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        string html = await _host.ClientAs(ConsoleHost.GlobalAdmin).GetStringAsync("/notifications", Ct);
        html.ShouldContain("<h1>Change notifications</h1>");
        html.ShouldContain("Working");
        html.ShouldContain("mail-app");
        html.ShouldContain("handler=Remove");
    }

    [Fact]
    public async Task An_unconfigured_store_is_explained()
    {
        _host.Subscriptions.ListAsync(null, Arg.Any<CancellationToken>()).Returns(Error.Unsupported("not configured"));

        (await _host.ClientAs(ConsoleHost.GlobalAdmin).GetStringAsync("/notifications", Ct)).ShouldContain("Change notifications are not configured for the console");
    }
}
