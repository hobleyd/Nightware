using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Application.MailboxSettings;
using Nightware.Application.Presence;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;
using Nightware.Domain.Presence;

namespace Nightware.Api.Tests.Features.Presence;

/// <summary>Phase 25: the presence routes through the real composition, with user status faked (ADR 0009 D6).</summary>
public sealed class PresenceEndpointsTests : IAsyncLifetime
{
    private readonly IUserStatusStore _store = Substitute.For<IUserStatusStore>();
    private readonly IUserAccountNames _names = Substitute.For<IUserAccountNames>();
    private readonly IAutoReplyStore _replies = Substitute.For<IAutoReplyStore>();
    private readonly IMailboxResolver _mailboxes = Substitute.For<IMailboxResolver>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => _host.AuthorizedClient();

    public async ValueTask InitializeAsync()
    {
        // /me is the caller; /users/{x} is x's mailbox, with "bob-id" being bob's and "me-id" the caller's.
        _mailboxes.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(c => c.ArgAt<string?>(0) switch
        {
            null => new MailboxRef(TestHost.UserEmail),
            "bob-id" => new MailboxRef("bob@example.com"),
            "me-id" => new MailboxRef(TestHost.UserEmail),
            "ghost" => Error.NotFound("The user 'ghost' was not found."),
            var other => new MailboxRef(other),
        });
        _names.AccountNameOfAsync("bob@example.com", Arg.Any<CancellationToken>()).Returns("bob");
        _names.AccountNameOfAsync("bob-id", Arg.Any<CancellationToken>()).Returns("bob");
        _names.AccountNameOfAsync("ghost", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));
        _names.UserOfAccountAsync("bob", Arg.Any<CancellationToken>()).Returns(new DirectoryUser("bob-id", "Bob", "bob@example.com"));
        _store.GetAsync("bob", Arg.Any<CancellationToken>()).Returns(new UserStatus(UserStatusType.DoNotDisturb) { Message = "Focus" });
        _store.GetOwnAsync(Arg.Any<CancellationToken>()).Returns(new UserStatus(UserStatusType.Busy)
        {
            MessageId = "call", Message = "In a call", Icon = PredefinedMessages.CallIcon,
        });
        _store.SetStatusAsync(Arg.Any<UserStatusType>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _store.SetPredefinedMessageAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _store.SetCustomMessageAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _store.ClearMessageAsync(Arg.Any<CancellationToken>()).Returns(Result.Success());
        _replies.GetAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>())
            .Returns(new AutoReplySetting(AutoReplyStatus.AlwaysEnabled) { InternalReplyMessage = "Back Monday" });
        _host = await TestHost.StartAsync(services =>
        {
            services.AddScoped(_ => _store);
            services.AddScoped(_ => _names);
            services.AddScoped(_ => _replies);
            services.AddScoped(_ => _mailboxes);
        });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<JsonObject> GetAsync(string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await Client.GetAsync(path, Ct);
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadObjectAsync();
    }

    private async Task<string> PostAsync(string path, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = body is null
            ? await Client.PostAsync(path, null, Ct)
            : await Client.PostAsJsonAsync(path, body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return text;
    }

    [Theory]
    [InlineData("/v1.0/me/presence")]
    [InlineData("/v1.0/users/me-id/presence")]
    [InlineData("/beta/me/presence")]
    public async Task The_callers_presence_is_their_own_status_with_out_of_office(string path)
    {
        var presence = await GetAsync(path);

        presence["id"]!.GetValue<string>().ShouldBe("user-1");
        presence["availability"]!.GetValue<string>().ShouldBe("Busy");
        presence["activity"]!.GetValue<string>().ShouldBe("InACall");
        presence.ContainsKey("statusMessage").ShouldBeFalse();
        presence["outOfOfficeSettings"]!.ToJsonString().ShouldBe("""{"message":"Back Monday","isOutOfOffice":true}""");
        await _store.DidNotReceiveWithAnyArgs().GetAsync(default!, Ct);
    }

    [Theory]
    [InlineData("/v1.0/users/bob-id/presence")]
    [InlineData("/v1.0/communications/presences/bob-id")]
    public async Task Another_users_presence_is_their_public_status(string path)
    {
        var presence = await GetAsync(path);

        presence["id"]!.GetValue<string>().ShouldBe("bob-id");
        presence["availability"]!.GetValue<string>().ShouldBe("DoNotDisturb");
        presence["statusMessage"]!.ToJsonString().ShouldBe("""{"message":{"content":"Focus","contentType":"text"}}""");
        presence["outOfOfficeSettings"]!["isOutOfOffice"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task The_callers_message_expiry_is_a_utc_date_time_time_zone()
    {
        _store.GetOwnAsync(Arg.Any<CancellationToken>()).Returns(new UserStatus(UserStatusType.Online)
        {
            Message = "Lunch", ClearAt = new DateTimeOffset(2026, 11, 23, 13, 0, 0, TimeSpan.Zero),
        });

        var presence = await GetAsync("/v1.0/me/presence?$select=statusMessage");

        presence["statusMessage"]!["expiryDateTime"]!.ToJsonString().ShouldBe("""{"dateTime":"2026-11-23T13:00:00.0000000","timeZone":"UTC"}""");
    }

    [Theory]
    [InlineData("/v1.0/users/ghost/presence")]
    [InlineData("/v1.0/communications/presences/ghost")]
    public async Task Nobody_is_not_found(string path) =>
        await GetAsync(path, HttpStatusCode.NotFound);

    [Fact]
    public async Task Presences_by_user_id_leave_out_nobody()
    {
        var text = await PostAsync("/v1.0/communications/getPresencesByUserId", new { ids = new[] { "bob-id", "ghost" } });

        var value = JsonNode.Parse(text)!["value"]!.AsArray();
        value.Select(static p => p!["id"]!.GetValue<string>()).ShouldBe(["bob-id"]);
    }

    [Theory]
    [InlineData("""{"ids":"bob-id"}""")]
    [InlineData("""{"ids":[1]}""")]
    [InlineData("""{"ids":[]}""")]
    [InlineData("""{}""")]
    public async Task Presences_by_user_id_need_a_list_of_ids(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("/v1.0/communications/getPresencesByUserId", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("/v1.0/me")]
    [InlineData("/v1.0/users/me-id")]
    public async Task Set_presence_answers_200_with_no_body(string user)
    {
        var text = await PostAsync(user + "/presence/setPresence", new { sessionId = "app", availability = "Busy", activity = "InAMeeting", expirationDuration = "PT1H" });

        text.ShouldBeEmpty();
        await _store.Received(1).SetStatusAsync(UserStatusType.Busy, Arg.Any<CancellationToken>());
        await _store.Received(1).SetPredefinedMessageAsync("meeting", Arg.Is<DateTimeOffset?>(d => d != null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_user_preferred_presence_sets_the_same_status()
    {
        await PostAsync("/v1.0/me/presence/setUserPreferredPresence", new { availability = "Offline", activity = "OffWork" });

        await _store.Received(1).SetStatusAsync(UserStatusType.Invisible, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("setPresence", """{"sessionId":"app","availability":"Available","activity":"Available","expirationDuration":"an hour"}""")]
    [InlineData("setPresence", """{"availability":"Available","activity":"Available"}""")]
    [InlineData("setUserPreferredPresence", """{"availability":"Available","activity":"Presenting"}""")]
    [InlineData("setUserPreferredPresence", """{"availability":3,"activity":"Available"}""")]
    [InlineData("clearPresence", """{}""")]
    [InlineData("setStatusMessage", """{}""")]
    [InlineData("setStatusMessage", """{"statusMessage":{"message":{"content":"hi"},"expiryDateTime":{"dateTime":"soon","timeZone":"UTC"}}}""")]
    public async Task A_bad_body_is_400(string action, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("/v1.0/me/presence/" + action, content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("setPresence")]
    [InlineData("setUserPreferredPresence")]
    [InlineData("setStatusMessage")]
    [InlineData("clearPresence")]
    public async Task A_missing_body_is_400(string action) =>
        await PostAsync("/v1.0/me/presence/" + action, null, HttpStatusCode.BadRequest);

    [Theory]
    [InlineData("setPresence", """{"sessionId":"app","availability":"Available","activity":"Available"}""")]
    [InlineData("setUserPreferredPresence", """{"availability":"Available","activity":"Available"}""")]
    [InlineData("clearPresence", """{"sessionId":"app"}""")]
    [InlineData("clearUserPreferredPresence", """{}""")]
    [InlineData("setStatusMessage", """{"statusMessage":{"message":{"content":"hi","contentType":"text"}}}""")]
    public async Task Setting_anyone_elses_presence_is_403(string action, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("/v1.0/users/bob-id/presence/" + action, content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync(Ct));
        await _store.DidNotReceiveWithAnyArgs().SetStatusAsync(default, Ct);
    }

    [Theory]
    [InlineData("/v1.0/me/presence/clearPresence", """{"sessionId":"app"}""")]
    [InlineData("/v1.0/users/me-id/presence/clearUserPreferredPresence", null)]
    [InlineData("/v1.0/me/presence/clearUserPreferredPresence", "{}")]
    public async Task Clearing_goes_back_online_without_the_activity_message(string path, string? body)
    {
        using var content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync(path, content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        await _store.Received(1).SetStatusAsync(UserStatusType.Online, Arg.Any<CancellationToken>());
        await _store.Received(1).ClearMessageAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_status_message_expiry_is_read_in_its_time_zone()
    {
        await PostAsync("/v1.0/me/presence/setStatusMessage", new
        {
            statusMessage = new
            {
                message = new { content = "Hey I'm currently in a meeting.", contentType = "text" },
                expiryDateTime = new { dateTime = "2030-01-18T17:05:33.0000000", timeZone = "Pacific Standard Time" },
            },
        });

        await _store.Received(1).SetCustomMessageAsync("Hey I'm currently in a meeting.", new DateTimeOffset(2030, 1, 19, 1, 5, 33, TimeSpan.Zero), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_html_status_message_is_reduced_to_text()
    {
        await PostAsync("/v1.0/me/presence/setStatusMessage", new
        {
            statusMessage = new { message = new { content = "<p>Out &amp; about</p>", contentType = "html" }, expiryDateTime = new { dateTime = "2030-01-18T17:05:33Z", timeZone = "UTC" } },
        });

        await _store.Received(1).SetCustomMessageAsync("Out & about", new DateTimeOffset(2030, 1, 18, 17, 5, 33, TimeSpan.Zero), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_backend_failure_is_the_graph_error()
    {
        _store.GetOwnAsync(Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        await GetAsync("/v1.0/me/presence", HttpStatusCode.ServiceUnavailable);
        await PostAsync("/v1.0/me/presence/clearUserPreferredPresence", null, HttpStatusCode.ServiceUnavailable);
    }
}
