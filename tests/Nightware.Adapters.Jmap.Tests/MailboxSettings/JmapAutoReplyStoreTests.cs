using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Jmap.MailboxSettings;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.MailboxSettings;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Adapters.Jmap.Tests.MailboxSettings;

public sealed class JmapAutoReplyStoreTests : IAsyncDisposable
{
    private static readonly MailboxRef Alice = new(JmapTestHost.AliceAddress);
    private readonly JmapTestHost _host = new();

    // The VacationResponse path: a mailbox without Nightware's Sieve script (JmapAutoReplyStoreScriptTests covers that).
    public JmapAutoReplyStoreTests() => _host.StubSession(JmapTestHost.SessionWithout("urn:ietf:params:jmap:sieve"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IAutoReplyStore Store() => _host.Services.CreateScope().ServiceProvider.GetRequiredService<IAutoReplyStore>();

    private static string Vacation(string fields) =>
        $$"""[["VacationResponse/get",{"accountId":"c","state":"1","list":[{"id":"singleton",{{fields}}}],"notFound":[]},"c0"]]""";

    [Fact]
    public async Task Is_registered_as_the_port()
    {
        await using var scope = _host.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IAutoReplyStore>().ShouldBeOfType<JmapAutoReplyStore>();
    }

    [Fact]
    public async Task Reads_the_singleton_with_the_vacationresponse_capability()
    {
        _host.StubApi(Vacation("""
            "isEnabled":true,"fromDate":"2026-09-19T14:00:00Z","toDate":"2026-09-27T13:59:59Z","subject":null,"textBody":"Away","htmlBody":"<p>Away</p>"
            """));

        var setting = (await Store().GetAsync(Alice, Ct)).Value;

        setting.Status.ShouldBe(AutoReplyStatus.Scheduled);
        setting.ExternalAudience.ShouldBe(ExternalAudience.All);
        setting.InternalReplyMessage.ShouldBe("<p>Away</p>");
        setting.ExternalReplyMessage.ShouldBe("<p>Away</p>");
        setting.ScheduledStartDateTime.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 9, 19, 14, 0, 0), "UTC"));
        setting.ScheduledEndDateTime.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 9, 27, 13, 59, 59), "UTC"));

        var body = _host.ApiBodies().Single();
        body["using"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldContain("urn:ietf:params:jmap:vacationresponse");
        var call = body["methodCalls"]![0]!.AsArray();
        call[0]!.GetValue<string>().ShouldBe("VacationResponse/get");
        call[1]!["accountId"]!.GetValue<string>().ShouldBe("c");
        call[1]!["ids"]!.AsArray().Single()!.GetValue<string>().ShouldBe("singleton");
    }

    [Theory]
    [InlineData("\"isEnabled\":false,\"fromDate\":\"2026-09-19T14:00:00Z\"", AutoReplyStatus.Disabled)]
    [InlineData("\"isEnabled\":true", AutoReplyStatus.AlwaysEnabled)]
    [InlineData("\"isEnabled\":true,\"toDate\":\"2026-09-27T13:59:59Z\"", AutoReplyStatus.Scheduled)]
    public async Task Derives_the_status(string fields, AutoReplyStatus status)
    {
        _host.StubApi(Vacation(fields));

        (await Store().GetAsync(Alice, Ct)).Value.Status.ShouldBe(status);
    }

    [Fact]
    public async Task A_text_only_reply_is_returned_as_html()
    {
        _host.StubApi(Vacation("\"isEnabled\":true,\"textBody\":\"Away <until> Monday\\nThanks\""));

        var setting = (await Store().GetAsync(Alice, Ct)).Value;

        setting.InternalReplyMessage.ShouldBe("Away &lt;until&gt; Monday<br>Thanks");
    }

    [Fact]
    public async Task A_missing_singleton_reads_as_disabled()
    {
        _host.StubApi("""[["VacationResponse/get",{"accountId":"c","state":"1","list":[],"notFound":["singleton"]},"c0"]]""");

        var setting = (await Store().GetAsync(Alice, Ct)).Value;

        setting.Status.ShouldBe(AutoReplyStatus.Disabled);
        setting.InternalReplyMessage.ShouldBeNull();
    }

    [Fact]
    public async Task A_shared_mailbox_without_the_capability_is_forbidden()
    {
        var result = await Store().GetAsync(new MailboxRef("bob@nightware.localhost"), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _host.ApiBodies().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_mailbox_is_not_found()
    {
        var result = await Store().GetAsync(new MailboxRef("carol@nightware.localhost"), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Method_errors_are_mapped()
    {
        _host.StubApi("""[["error",{"type":"forbidden"},"c0"]]""");

        var result = await Store().GetAsync(Alice, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Http_failures_are_mapped()
    {
        _host.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/jmap/").UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(503));

        var result = await Store().GetAsync(Alice, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Writes_a_schedule_as_utc_dates_and_the_external_message()
    {
        _host.StubApi("""
            [["VacationResponse/set",{"accountId":"c","oldState":"1","newState":"2","updated":{"singleton":null}},"c0"],
             ["VacationResponse/get",{"accountId":"c","state":"2","list":[{"id":"singleton","isEnabled":true,"fromDate":"2026-09-19T14:00:00Z","toDate":"2026-09-27T13:59:59Z","htmlBody":"<div>Out of the office.</div>"}],"notFound":[]},"c1"]]
            """);
        var setting = new AutoReplySetting(AutoReplyStatus.Scheduled)
        {
            ExternalAudience = ExternalAudience.ContactsOnly,
            InternalReplyMessage = "<div>Away.</div>",
            ExternalReplyMessage = "<div>Out of the office.</div>",
            ScheduledStartDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 20), "AUS Eastern Standard Time"),
            ScheduledEndDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 27, 23, 59, 59), "AUS Eastern Standard Time"),
        };

        var stored = (await Store().SetAsync(Alice, setting, ["example.com"], Ct)).Value;

        stored.Status.ShouldBe(AutoReplyStatus.Scheduled);
        stored.ExternalReplyMessage.ShouldBe("<div>Out of the office.</div>");
        var calls = _host.ApiBodies().Single()["methodCalls"]!.AsArray();
        calls.Select(c => c![0]!.GetValue<string>()).ShouldBe(["VacationResponse/set", "VacationResponse/get"]);
        var patch = calls[0]![1]!["update"]!["singleton"]!.AsObject();
        JsonNode.DeepEquals(patch, JsonNode.Parse("""
            {"isEnabled":true,"fromDate":"2026-09-19T14:00:00Z","toDate":"2026-09-27T13:59:59Z",
             "htmlBody":"<div>Out of the office.</div>","textBody":"Out of the office."}
            """)).ShouldBeTrue(patch.ToJsonString());
    }

    [Fact]
    public async Task Always_enabled_clears_the_dates()
    {
        _host.StubApi("""
            [["VacationResponse/set",{"accountId":"c","newState":"2","updated":{"singleton":null}},"c0"],
             ["VacationResponse/get",{"accountId":"c","state":"2","list":[{"id":"singleton","isEnabled":true,"htmlBody":"x"}],"notFound":[]},"c1"]]
            """);
        var setting = new AutoReplySetting(AutoReplyStatus.AlwaysEnabled)
        {
            InternalReplyMessage = "<p>In only</p>",
            ScheduledStartDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 20), "UTC"),
        };

        (await Store().SetAsync(Alice, setting, ["example.com"], Ct)).Value.Status.ShouldBe(AutoReplyStatus.AlwaysEnabled);

        var patch = _host.ApiBodies().Single()["methodCalls"]![0]![1]!["update"]!["singleton"]!.AsObject();
        patch["isEnabled"]!.GetValue<bool>().ShouldBeTrue();
        patch["fromDate"].ShouldBeNull();
        patch.ContainsKey("fromDate").ShouldBeTrue();
        patch["htmlBody"]!.GetValue<string>().ShouldBe("<p>In only</p>");
    }

    [Fact]
    public async Task Disabling_keeps_the_message()
    {
        _host.StubApi("""
            [["VacationResponse/set",{"accountId":"c","newState":"2","updated":{"singleton":null}},"c0"],
             ["VacationResponse/get",{"accountId":"c","state":"2","list":[{"id":"singleton","isEnabled":false,"htmlBody":"<p>Away</p>"}],"notFound":[]},"c1"]]
            """);

        var stored = (await Store().SetAsync(Alice, new AutoReplySetting(AutoReplyStatus.Disabled) { ExternalReplyMessage = "<p>Away</p>" }, ["example.com"], Ct)).Value;

        stored.Status.ShouldBe(AutoReplyStatus.Disabled);
        _host.ApiBodies().Single()["methodCalls"]![0]![1]!["update"]!["singleton"]!["isEnabled"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task A_rejected_update_is_mapped()
    {
        _host.StubApi("""
            [["VacationResponse/set",{"accountId":"c","newState":"1","notUpdated":{"singleton":{"type":"invalidProperties","properties":["fromDate"]}}},"c0"],
             ["VacationResponse/get",{"accountId":"c","state":"1","list":[],"notFound":[]},"c1"]]
            """);

        var result = await Store().SetAsync(Alice, new AutoReplySetting(AutoReplyStatus.Disabled), ["example.com"], Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task A_method_error_on_set_is_mapped()
    {
        _host.StubApi("""[["error",{"type":"accountReadOnly"},"c0"],["VacationResponse/get",{"accountId":"c","state":"1","list":[],"notFound":[]},"c1"]]""");

        var result = await Store().SetAsync(Alice, new AutoReplySetting(AutoReplyStatus.Disabled), ["example.com"], Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_read_only_account_is_refused_without_a_request()
    {
        var session = JsonNode.Parse(Fixtures.AliceSession)!.AsObject();
        session["accounts"]!["f"]!["accountCapabilities"]!.AsObject()["urn:ietf:params:jmap:vacationresponse"] = new JsonObject();
        await using var host = new JmapTestHost();
        host.StubSession(session.ToJsonString());
        var store = host.Services.CreateScope().ServiceProvider.GetRequiredService<IAutoReplyStore>();

        var result = await store.SetAsync(new MailboxRef("reception@nightware.localhost"), new AutoReplySetting(AutoReplyStatus.Disabled), ["example.com"], Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        host.ApiBodies().ShouldBeEmpty();
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
