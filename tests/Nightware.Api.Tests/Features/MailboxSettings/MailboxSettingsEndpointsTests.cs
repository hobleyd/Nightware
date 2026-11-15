using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Directory;
using Nightware.Application.MailboxSettings;
using Nightware.Domain.Calendar;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Api.Tests.Features.MailboxSettings;

public sealed class MailboxSettingsEndpointsTests : IAsyncLifetime
{
    private static readonly AutoReplySetting Stored = new(AutoReplyStatus.Scheduled)
    {
        ExternalAudience = ExternalAudience.All,
        InternalReplyMessage = "<p>Away</p>",
        ExternalReplyMessage = "<p>Away</p>",
        ScheduledStartDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 19, 14, 0, 0), "UTC"),
        ScheduledEndDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 27, 13, 59, 59), "UTC"),
    };

    private readonly IAutoReplyStore _replies = Substitute.For<IAutoReplyStore>();
    private readonly IUserPreferencesStore _preferences = Substitute.For<IUserPreferencesStore>();
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await TestHost.StartAsync(services => services.AddSingleton(_replies).AddSingleton(_preferences));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/v1.0/me", TestHost.UserEmail)]
    [InlineData("/v1.0/users/shared@example.com", "shared@example.com")]
    [InlineData("/v1.0/users/shared%40example.com", "shared@example.com")]
    public async Task Reads_automatic_replies_and_time_zone_in_both_forms(string prefix, string mailbox)
    {
        _replies.GetAsync(new MailboxRef(mailbox), Arg.Any<CancellationToken>()).Returns(Stored);
        _preferences.GetAsync(new MailboxRef(mailbox), Arg.Any<CancellationToken>()).Returns(new UserPreferences("Australia/Sydney", "en"));

        var response = await _host.AuthorizedClient().GetAsync(prefix + "/mailboxSettings?$select=automaticRepliesSetting,timeZone", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body.Remove("@odata.context");
        JsonNode.DeepEquals(body, JsonNode.Parse("""
            {"timeZone":"AUS Eastern Standard Time",
             "automaticRepliesSetting":{"status":"scheduled","externalAudience":"all","internalReplyMessage":"<p>Away</p>","externalReplyMessage":"<p>Away</p>",
               "scheduledStartDateTime":{"dateTime":"2026-09-20T00:00:00.0000000","timeZone":"AUS Eastern Standard Time"},
               "scheduledEndDateTime":{"dateTime":"2026-09-27T23:59:59.0000000","timeZone":"AUS Eastern Standard Time"}}}
            """)).ShouldBeTrue(body.ToJsonString());
    }

    [Fact]
    public async Task Select_projects_the_response()
    {
        _replies.GetAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>()).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _preferences.GetAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>()).Returns(new UserPreferences(null, null));

        var body = await (await _host.AuthorizedClient().GetAsync("/v1.0/me/mailboxSettings?$select=timeZone", Ct)).ReadObjectAsync();

        body.ContainsKey("automaticRepliesSetting").ShouldBeFalse();
        body.Str("timeZone").ShouldBe("UTC");
    }

    [Fact]
    public async Task A_mailbox_the_caller_cannot_open_is_403()
    {
        _replies.GetAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>()).Returns(Error.Forbidden("no"));

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/users/bob@example.com/mailboxSettings", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/v1.0/me", TestHost.UserEmail)]
    [InlineData("/v1.0/users/shared@example.com", "shared@example.com")]
    public async Task Patches_the_automatic_replies_as_the_client_sends_them(string prefix, string mailbox)
    {
        _replies.SetAsync(new MailboxRef(mailbox), Arg.Is<AutoReplySetting>(s => s.Status == AutoReplyStatus.Scheduled
                && s.ExternalAudience == ExternalAudience.ContactsOnly
                && s.InternalReplyMessage == "<div>Away.</div>" && s.ExternalReplyMessage == "<div>Out.</div>"
                && s.ScheduledStartDateTime == new DateTimeTimeZone(new DateTime(2026, 9, 20), "AUS Eastern Standard Time")
                && s.ScheduledEndDateTime == null), Arg.Any<CancellationToken>())
            .Returns(Stored);
        _preferences.GetAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>()).Returns(new UserPreferences("Australia/Sydney", null));

        var response = await _host.AuthorizedClient().PatchAsync(prefix + "/mailboxSettings", Json("""
            {"automaticRepliesSetting":{"status":"scheduled","externalAudience":"contactsOnly","internalReplyMessage":"<div>Away.</div>","externalReplyMessage":"<div>Out.</div>",
              "scheduledStartDateTime":{"dateTime":"2026-09-20T00:00:00.0000000","timeZone":"AUS Eastern Standard Time"}}}
            """), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body.ContainsKey("automaticRepliesSetting").ShouldBeTrue();
        body.ContainsKey("timeZone").ShouldBeFalse("only the patched properties are echoed");
    }

    [Fact]
    public async Task Graphs_unset_schedule_is_read_as_none()
    {
        _replies.SetAsync(Arg.Any<MailboxRef>(), Arg.Is<AutoReplySetting>(s => s.ScheduledStartDateTime == null), Arg.Any<CancellationToken>())
            .Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _preferences.GetAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>()).Returns(new UserPreferences(null, null));

        var response = await _host.AuthorizedClient().PatchAsync("/v1.0/me/mailboxSettings", Json("""
            {"automaticRepliesSetting":{"status":"disabled","scheduledStartDateTime":{"dateTime":"0001-01-01T00:00:00.0000000","timeZone":"UTC"}}}
            """), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Patches_the_time_zone_of_the_callers_mailbox()
    {
        _replies.GetAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>()).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _preferences.SetAsync(new MailboxRef(TestHost.UserEmail), new UserPreferencesPatch { TimeZone = "Europe/London" }, Arg.Any<CancellationToken>())
            .Returns(new UserPreferences("Europe/London", null));

        var response = await _host.AuthorizedClient().PatchAsync("/v1.0/me/mailboxSettings", Json("""{"timeZone":"GMT Standard Time"}"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body.Str("timeZone").ShouldBe("GMT Standard Time");
        body.ContainsKey("automaticRepliesSetting").ShouldBeFalse();
    }

    [Fact]
    public async Task Another_mailboxs_time_zone_is_403()
    {
        var response = await _host.AuthorizedClient().PatchAsync("/v1.0/users/bob@example.com/mailboxSettings", Json("""{"timeZone":"UTC"}"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await _preferences.DidNotReceiveWithAnyArgs().SetAsync(default, null!, Ct);
    }

    [Theory]
    [InlineData("""{"language":{"locale":"en-AU"}}""")]
    [InlineData("""{"workingHours":{}}""")]
    [InlineData("""{"automaticRepliesSetting":{"status":"sometimes"}}""")]
    [InlineData("""{"automaticRepliesSetting":"on"}""")]
    [InlineData("""{"timeZone":5}""")]
    [InlineData("""{"timeZone":""}""")]
    [InlineData("""{}""")]
    public async Task Rejects_what_it_cannot_store(string json)
    {
        var response = await _host.AuthorizedClient().PatchAsync("/v1.0/me/mailboxSettings", Json(json), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _replies.DidNotReceiveWithAnyArgs().SetAsync(default, null!, Ct);
    }

    [Fact]
    public async Task OData_annotations_in_the_body_are_ignored()
    {
        _replies.SetAsync(Arg.Any<MailboxRef>(), Arg.Any<AutoReplySetting>(), Arg.Any<CancellationToken>()).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _preferences.GetAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>()).Returns(new UserPreferences(null, null));

        var response = await _host.AuthorizedClient().PatchAsync("/v1.0/me/mailboxSettings", Json("""{"@odata.context":"x","automaticRepliesSetting":{"status":"disabled"}}"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}
