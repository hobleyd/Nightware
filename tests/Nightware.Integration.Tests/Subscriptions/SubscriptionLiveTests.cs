using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Subscriptions;

/// <summary>
/// Phase 16 subscriptions against the running stack: Keycloak issues the subscriber's refresh token, the store is a
/// throwaway Postgres, and the notification endpoint is a loopback receiver named as a test-only origin.
/// </summary>
public sealed class SubscriptionLiveTests : IAsyncLifetime
{
    private SubscriptionDatabase? _database;
    private NotificationReceiver? _receiver;
    private LiveStack? _stack;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (!DevStack.Enabled)
        {
            return;
        }

        _database = await SubscriptionDatabase.StartAsync(Ct);
        _receiver = new NotificationReceiver();
        _stack = LiveStack.Start(new Dictionary<string, string?>
        {
            ["Subscriptions:ConnectionString"] = _database.ConnectionString,
            ["Subscriptions:SealingKey"] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            ["Subscriptions:TestOnlyAllowedOrigins:0"] = _receiver.Origin,
            ["Subscriptions:PollInterval"] = "00:00:02",
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_stack is not null)
        {
            await _stack.DisposeAsync();
        }

        _receiver?.Dispose();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string url, string? json = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    [Fact]
    public async Task A_subscription_is_validated_stored_renewed_and_deleted()
    {
        Assert.SkipUnless(DevStack.Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var alice = await _stack!.GraphClientAsync("alice", Ct);
        var bob = await _stack.GraphClientAsync("bob", Ct);
        var expiry = DateTimeOffset.UtcNow.AddHours(2);

        var created = await SendAsync(alice, HttpMethod.Post, "/v1.0/subscriptions", $$"""
            {"changeType":"created,updated,deleted","notificationUrl":"{{_receiver!.NotifyUrl}}","resource":"me/mailFolders('Inbox')/messages",
             "expirationDateTime":"{{expiry:O}}","clientState":"live-state"}
            """, HttpStatusCode.Created);
        var id = created["id"]!.GetValue<string>();
        _receiver.Validations.ShouldBe(1);
        created["clientState"]!.GetValue<string>().ShouldBe("live-state");
        created["applicationId"]!.GetValue<string>().ShouldBe("nightware-dev");

        var listed = (await SendAsync(alice, HttpMethod.Get, "/v1.0/subscriptions"))["value"]!.AsArray().ShouldHaveSingleItem()!;
        listed["id"]!.GetValue<string>().ShouldBe(id);
        listed["clientState"].ShouldBeNull();
        (await SendAsync(bob, HttpMethod.Get, "/v1.0/subscriptions"))["value"]!.AsArray().ShouldBeEmpty();
        await SendAsync(bob, HttpMethod.Get, $"/v1.0/subscriptions/{id}", expected: HttpStatusCode.NotFound);

        var renewed = await SendAsync(alice, HttpMethod.Patch, $"/v1.0/subscriptions/{id}", $$"""{"expirationDateTime":"{{expiry.AddDays(1):O}}"}""");
        renewed["expirationDateTime"]!.GetValue<DateTimeOffset>().ShouldBe(expiry.AddDays(1), TimeSpan.FromSeconds(1));

        await SendAsync(alice, HttpMethod.Delete, $"/v1.0/subscriptions/{id}", expected: HttpStatusCode.NoContent);
        await SendAsync(alice, HttpMethod.Get, $"/v1.0/subscriptions/{id}", expected: HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("https://10.0.0.1/notify")]
    [InlineData("https://127.0.0.1/notify")]
    [InlineData("https://localhost/notify")]
    [InlineData("http://hooks.example.com/notify")]
    public async Task A_notification_url_that_is_not_public_https_is_refused(string url)
    {
        Assert.SkipUnless(DevStack.Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var alice = await _stack!.GraphClientAsync("alice", Ct);

        var error = await SendAsync(alice, HttpMethod.Post, "/v1.0/subscriptions", $$"""
            {"changeType":"created","notificationUrl":"{{url}}","resource":"me/events","expirationDateTime":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"}
            """, HttpStatusCode.BadRequest);

        error["error"]!["message"]!.GetValue<string>().ShouldNotContain("validation request failed");
        (await SendAsync(alice, HttpMethod.Get, "/v1.0/subscriptions"))["value"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_delivery_to_the_inbox_is_notified_as_created_within_30_seconds()
    {
        Assert.SkipUnless(DevStack.Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var alice = await _stack!.GraphClientAsync("alice", Ct);
        var bob = await _stack.GraphClientAsync("bob", Ct);
        var run = Guid.NewGuid().ToString("N")[..8];
        var created = await SendAsync(alice, HttpMethod.Post, "/v1.0/subscriptions", $$"""
            {"changeType":"created","notificationUrl":"{{_receiver!.NotifyUrl}}","resource":"me/mailFolders('Inbox')/messages",
             "expirationDateTime":"{{DateTimeOffset.UtcNow.AddHours(1):O}}","clientState":"inbox-{{run}}"}
            """, HttpStatusCode.Created);
        var id = created["id"]!.GetValue<string>();
        try
        {
            await SendAsync(bob, HttpMethod.Post, "/v1.0/me/sendMail", $$$"""
                {"message":{"subject":"Notify {{{run}}}","body":{"contentType":"text","content":"hello"},
                  "toRecipients":[{"emailAddress":{"address":"{{{DevStack.Address("alice")}}}"}}]},"saveToSentItems":false}
                """, HttpStatusCode.Accepted);

            JsonObject? notification = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (notification is null && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(500, Ct);
                notification = _receiver.Notifications.Select(n => JsonNode.Parse(n)!["value"]!.AsArray())
                    .SelectMany(v => v).OfType<JsonObject>()
                    .FirstOrDefault(n => n["subscriptionId"]!.GetValue<string>() == id && n["changeType"]!.GetValue<string>() == "created");
            }

            notification.ShouldNotBeNull("a created notification within 30 s");
            notification["clientState"]!.GetValue<string>().ShouldBe($"inbox-{run}");
            var messageId = notification["resourceData"]!["id"]!.GetValue<string>();
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/messages/{messageId}?$select=subject"))["subject"]!.GetValue<string>().ShouldBe($"Notify {run}");
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/{notification["resource"]!.GetValue<string>()}?$select=subject"))["subject"]!.GetValue<string>().ShouldBe($"Notify {run}");
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/subscriptions/{id}", expected: HttpStatusCode.NoContent);
        }
    }
}
