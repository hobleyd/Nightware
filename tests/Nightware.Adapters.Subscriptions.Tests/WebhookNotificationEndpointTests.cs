using System.Text.Json.Nodes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Subscriptions;
using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.Subscriptions.Tests;

public sealed class WebhookNotificationEndpointTests : IDisposable
{
    private readonly WireMockServer _receiver = WireMockServer.Start();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _receiver.Dispose();

    /// <param name="allowLoopback">Whether the WireMock origin is exempt from the address guard (as only a test sets it).</param>
    private INotificationEndpoint Endpoint(bool allowLoopback = true, string timeout = "00:00:02")
    {
        var settings = new Dictionary<string, string?>
        {
            ["Subscriptions:ConnectionString"] = "Host=unused",
            ["Subscriptions:SealingKey"] = new string('a', 64),
            ["Subscriptions:EndpointTimeout"] = timeout,
        };
        if (allowLoopback)
        {
            settings["Subscriptions:TestOnlyAllowedOrigins:0"] = _receiver.Url;
        }

        var services = new ServiceCollection().AddLogging();
        services.AddSubscriptionsAdapter(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<INotificationEndpoint>();
    }

    private Uri Notify => new($"{_receiver.Url}/notify");

    private void Echo() =>
        _receiver.Given(Request.Create().WithPath("/notify").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/plain").WithCallback(r => new WireMock.ResponseMessage
            {
                StatusCode = 200,
                BodyData = new WireMock.Util.BodyData { BodyAsString = r.Query!["validationToken"][0], DetectedBodyType = WireMock.Types.BodyType.String },
            }));

    [Fact]
    public async Task The_handshake_is_sent_as_graph_sends_it_and_an_echo_passes()
    {
        Echo();

        (await Endpoint().ValidateAsync(Notify, Ct)).IsSuccess.ShouldBeTrue();

        // The captured handshake (subscriptions.validation): POST, validationToken in the query, text/plain, no body.
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "subscriptions.validation.json")))!["request"]!;
        var sent = _receiver.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        sent.Method.ShouldBe(fixture["method"]!.GetValue<string>());
        sent.Query!["validationToken"][0].ShouldStartWith("Validation: Testing client application reachability for subscription Request-Id: ");
        sent.Headers!["Content-Type"][0].ShouldStartWith("text/plain");
        sent.Body.ShouldBeNullOrEmpty();
        sent.Headers.ContainsKey("Authorization").ShouldBeFalse();
    }

    [Fact]
    public async Task A_wrong_answer_fails_validation()
    {
        _receiver.Given(Request.Create().WithPath("/notify").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBody("nope"));

        (await Endpoint().ValidateAsync(Notify, Ct)).Error!.Message.ShouldBe(WebhookNotificationEndpoint.ValidationFailed);
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        _receiver.Given(Request.Create().WithPath("/notify").UsingPost()).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", "https://example.com/"));

        (await Endpoint().ValidateAsync(Notify, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _receiver.LogEntries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Loopback_is_refused_unless_it_is_a_test_only_origin_and_plain_http_is_refused()
    {
        Echo();

        var refused = await Endpoint(allowLoopback: false).ValidateAsync(Notify, Ct);
        refused.Error!.Kind.ShouldBe(ErrorKind.Validation);
        refused.Error.Message.ShouldContain("HTTPS");
        (await Endpoint(allowLoopback: false).ValidateAsync(new Uri("https://127.0.0.1:1/notify"), Ct)).Error!.Message.ShouldContain("public address");
        (await Endpoint(allowLoopback: false).ValidateAsync(new Uri("https://10.0.0.1/notify"), Ct)).Error!.Message.ShouldContain("public address");
        _receiver.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_url_with_a_query_keeps_it_and_gains_the_token()
    {
        Echo();

        (await Endpoint().ValidateAsync(new Uri($"{_receiver.Url}/notify?tenant=a"), Ct)).IsSuccess.ShouldBeTrue();

        var query = _receiver.LogEntries.ShouldHaveSingleItem().RequestMessage!.Query!;
        query["tenant"][0].ShouldBe("a");
        query.ShouldContainKey("validationToken");
    }

    [Fact]
    public async Task A_name_that_resolves_to_loopback_is_refused_after_resolution()
    {
        var endpoint = Endpoint(allowLoopback: false);

        (await endpoint.ValidateAsync(new Uri("https://localhost:1/notify"), Ct)).Error!.Message.ShouldContain("public address");
        (await endpoint.DeliverAsync(new Uri("https://localhost:1/notify"), [Created("a")], Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task An_endpoint_slower_than_the_timeout_fails_validation_and_delivery()
    {
        _receiver.Given(Request.Create().WithPath("/notify").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));
        var endpoint = Endpoint(timeout: "00:00:00.200");

        (await endpoint.ValidateAsync(Notify, Ct)).Error!.Message.ShouldBe(WebhookNotificationEndpoint.ValidationFailed);
        (await endpoint.DeliverAsync(Notify, [Created("a")], Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_mistaken_for_a_failed_endpoint()
    {
        _receiver.Given(Request.Create().WithPath("/notify").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Endpoint().ValidateAsync(Notify, cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => Endpoint().DeliverAsync(Notify, [Created("a")], cancelled.Token));
    }

    private static ChangeNotification Created(string id) =>
        new("00000000-0000-0000-0000-000000000001", new DateTimeOffset(2026, 11, 20, 23, 40, 0, TimeSpan.Zero), "created",
            $"Users/me@example.com/Messages/{id}", "#Microsoft.Graph.Message", id) { ClientState = "nightware-capture-state" };

    [Fact]
    public void The_notification_has_the_captured_shape()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "subscriptions.notification.json")))!["request"]!["body"]!["value"]![0]!.AsObject();
        var built = JsonNode.Parse(WebhookNotificationEndpoint.Serialize([Created("AAMk-id-0002=")]))!["value"]!.AsArray().ShouldHaveSingleItem()!.AsObject();

        // Nightware has no tenants and no change keys: tenantId and the resourceData etag are left out.
        built.Select(p => p.Key).ShouldBe(fixture.Select(p => p.Key).Where(k => k != "tenantId"));
        built["resourceData"]!.AsObject().Select(p => p.Key).ShouldBe(fixture["resourceData"]!.AsObject().Select(p => p.Key).Where(k => k != "@odata.etag"));
        foreach (var key in new[] { "subscriptionId", "subscriptionExpirationDateTime", "changeType", "clientState" })
        {
            built[key]!.GetValue<string>().ShouldBe(fixture[key]!.GetValue<string>());
        }

        built["resourceData"]!["@odata.type"]!.GetValue<string>().ShouldBe(fixture["resourceData"]!["@odata.type"]!.GetValue<string>());
        built["resourceData"]!["id"]!.GetValue<string>().ShouldBe("AAMk-id-0002=");
    }

    [Fact]
    public async Task Notifications_are_posted_as_json_and_a_2xx_is_delivery()
    {
        _receiver.Given(Request.Create().WithPath("/notify").UsingPost()).RespondWith(Response.Create().WithStatusCode(202));

        (await Endpoint().DeliverAsync(Notify, [Created("a"), Created("b")], Ct)).IsSuccess.ShouldBeTrue();

        var sent = _receiver.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        sent.Headers!["Content-Type"][0].ShouldStartWith("application/json");
        sent.Headers.ContainsKey("Authorization").ShouldBeFalse();
        JsonNode.Parse(sent.Body!)!["value"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_refusal_or_an_unreachable_endpoint_is_not_delivery()
    {
        _receiver.Given(Request.Create().WithPath("/notify").UsingPost()).RespondWith(Response.Create().WithStatusCode(500));

        (await Endpoint().DeliverAsync(Notify, [Created("a")], Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        (await Endpoint().DeliverAsync(new Uri("https://10.0.0.1/n"), [Created("a")], Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        (await Endpoint(allowLoopback: false).DeliverAsync(Notify, [Created("a")], Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task A_lifecycle_notification_is_one_item_naming_its_event()
    {
        _receiver.Given(Request.Create().WithPath("/notify").UsingPost()).RespondWith(Response.Create().WithStatusCode(202));
        var notification = new LifecycleNotification("s1", new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero), LifecycleNotification.ReauthorizationRequired) { ClientState = "c" };

        (await Endpoint().DeliverLifecycleAsync(Notify, notification, Ct)).IsSuccess.ShouldBeTrue();

        _receiver.LogEntries.Single().RequestMessage!.Body.ShouldBe(
            """{"value":[{"subscriptionId":"s1","subscriptionExpirationDateTime":"2026-11-22T00:00:00+00:00","lifecycleEvent":"reauthorizationRequired","clientState":"c"}]}""");
    }

    [Fact]
    public async Task Without_a_client_state_none_is_sent()
    {
        _receiver.Given(Request.Create().WithPath("/notify").UsingPost()).RespondWith(Response.Create().WithStatusCode(202));
        var lifecycle = new LifecycleNotification("s1", new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero), LifecycleNotification.Missed);

        (await Endpoint().DeliverLifecycleAsync(Notify, lifecycle, Ct)).IsSuccess.ShouldBeTrue();

        _receiver.LogEntries.Single().RequestMessage!.Body!.ShouldNotContain("clientState");
        WebhookNotificationEndpoint.Serialize([Created("a") with { ClientState = null }]).ShouldNotContain("clientState");
    }
}
