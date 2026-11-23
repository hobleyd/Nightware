using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Dav.Presence;
using Nightware.Application.Common.Identity;
using Nightware.Application.Presence;
using Nightware.Domain.Kernel;
using Nightware.Domain.Presence;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Presence;

/// <summary>The user status store against a WireMock Nextcloud serving responses recorded from the dev stack (Nextcloud 35).</summary>
public sealed class NextcloudUserStatusStoreTests : IDisposable
{
    private const string Api = "/ocs/v2.php/apps/user_status/api/v1/";
    private const string Own = Api + "user_status";
    private readonly DavTestServer _nc = new();

    public void Dispose() => _nc.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NextcloudUserStatusStore Store => new(_nc.Client);

    private static string Recorded(string name) => Fixture.Read(Path.Combine("Presence", name));

    private void Serve(string method, string path, int status, string body) =>
        _nc.Server.Given(Request.Create().WithPath(path).UsingMethod(method))
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json; charset=utf-8").WithBody(body));

    private string SentBody(string method, string path)
    {
        var entry = _nc.Server.LogEntries.Single(e => e.RequestMessage!.Method == method && e.RequestMessage!.Path == path);
        entry.RequestMessage!.Headers!["OCS-APIRequest"].ShouldBe(["true"]);
        return entry.RequestMessage!.Body ?? string.Empty;
    }

    [Fact]
    public void Is_registered_by_the_dav_adapter()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dav:BaseUrl"] = "http://nextcloud/" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<IBackendCredentials>());
        services.AddDavAdapter(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        provider.CreateScope().ServiceProvider.GetRequiredService<IUserStatusStore>().ShouldBeOfType<NextcloudUserStatusStore>();
    }

    [Fact]
    public async Task The_callers_own_status_carries_the_predefined_message_id()
    {
        Serve("GET", Own, 200, Recorded("own-call.json"));

        var status = (await Store.GetOwnAsync(Ct)).Value;

        status.ShouldBe(new UserStatus(UserStatusType.Busy) { MessageId = "call", Message = "In a call", Icon = PredefinedMessages.CallIcon });
        _nc.Server.LogEntries.Single().RequestMessage!.Query!["format"].ShouldBe(["json"]);
    }

    [Fact]
    public async Task A_custom_message_has_its_clear_at()
    {
        Serve("GET", Own, 200, Recorded("own-custom.json"));

        var status = (await Store.GetOwnAsync(Ct)).Value;

        status.ShouldBe(new UserStatus(UserStatusType.DoNotDisturb) { Message = "Focus time", ClearAt = DateTimeOffset.FromUnixTimeSeconds(1795426506) });
    }

    [Fact]
    public async Task Another_users_public_status_has_no_message_id()
    {
        Serve("GET", Api + "statuses/bob", 200, Recorded("public-meeting.json"));

        var status = (await Store.GetAsync("bob", Ct)).Value;

        status.ShouldBe(new UserStatus(UserStatusType.Busy) { Message = "In a meeting", Icon = PredefinedMessages.MeetingIcon, ClearAt = DateTimeOffset.FromUnixTimeSeconds(1795430054) });
    }

    [Fact]
    public async Task An_account_name_is_escaped_in_the_path()
    {
        Serve("GET", Api + "statuses/j%20doe", 200, Recorded("public-meeting.json"));

        (await Store.GetAsync("j doe", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task No_status_row_reads_as_none()
    {
        Serve("GET", Api + "statuses/carol", 404, Recorded("no-status.json"));
        Serve("GET", Own, 404, Recorded("no-status.json"));

        (await Store.GetAsync("carol", Ct)).Value.ShouldBeSameAs(UserStatus.None);
        (await Store.GetOwnAsync(Ct)).Value.ShouldBeSameAs(UserStatus.None);
    }

    [Theory]
    [InlineData(503, ErrorKind.BackendUnavailable)]
    [InlineData(403, ErrorKind.Forbidden)]
    public async Task Other_failures_pass_through(int status, ErrorKind kind)
    {
        Serve("GET", Own, status, "{}");

        (await Store.GetOwnAsync(Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Theory]
    [InlineData("""{"ocs":{"meta":{"statuscode":200},"data":[]}}""")]
    [InlineData("""{"ocs":{"meta":{"statuscode":200},"data":{"status":"lunch"}}}""")]
    public async Task An_unexpected_answer_is_unavailable(string body)
    {
        Serve("GET", Own, 200, body);

        (await Store.GetOwnAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Theory]
    [InlineData(UserStatusType.Online, "online")]
    [InlineData(UserStatusType.Away, "away")]
    [InlineData(UserStatusType.DoNotDisturb, "dnd")]
    [InlineData(UserStatusType.Busy, "busy")]
    [InlineData(UserStatusType.Invisible, "invisible")]
    [InlineData(UserStatusType.Offline, "offline")]
    public async Task Setting_the_status_puts_its_type(UserStatusType type, string name)
    {
        Serve("PUT", Own + "/status", 200, Recorded("set-status.json"));

        (await Store.SetStatusAsync(type, Ct)).IsSuccess.ShouldBeTrue();

        SentBody("PUT", Own + "/status").ShouldBe("statusType=" + name);
    }

    [Fact]
    public async Task A_refused_status_is_a_validation_error_with_nextclouds_reason()
    {
        Serve("PUT", Own + "/status", 400, Recorded("bad-status.json"));

        var error = (await Store.SetStatusAsync(UserStatusType.Busy, Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Message.ShouldContain("not supported");
    }

    [Fact]
    public async Task A_predefined_message_is_put_with_its_clear_at_in_unix_seconds()
    {
        Serve("PUT", Own + "/message/predefined", 200, Recorded("set-status.json"));

        (await Store.SetPredefinedMessageAsync("meeting", DateTimeOffset.FromUnixTimeSeconds(1795430054), Ct)).IsSuccess.ShouldBeTrue();
        (await Store.SetPredefinedMessageAsync("call", null, Ct)).IsSuccess.ShouldBeTrue();

        var bodies = _nc.Server.LogEntries.Select(static e => e.RequestMessage!.Body).ToList();
        bodies.ShouldBe(["messageId=meeting&clearAt=1795430054", "messageId=call"]);
    }

    [Fact]
    public async Task A_custom_message_is_put_form_encoded()
    {
        Serve("PUT", Own + "/message/custom", 200, Recorded("own-custom.json"));

        (await Store.SetCustomMessageAsync("Back at 3 & then some", DateTimeOffset.FromUnixTimeSeconds(1795426506), Ct)).IsSuccess.ShouldBeTrue();

        SentBody("PUT", Own + "/message/custom").ShouldBe("message=Back+at+3+%26+then+some&clearAt=1795426506");
    }

    [Fact]
    public async Task Clearing_the_message_deletes_it()
    {
        Serve("DELETE", Own + "/message", 200, """{"ocs":{"meta":{"status":"ok","statuscode":200,"message":"OK"},"data":[]}}""");

        (await Store.ClearMessageAsync(Ct)).IsSuccess.ShouldBeTrue();

        _nc.Server.LogEntries.Single().RequestMessage!.Method.ShouldBe("DELETE");
    }

    [Fact]
    public async Task A_failed_write_passes_through()
    {
        Serve("DELETE", Own + "/message", 503, "{}");

        (await Store.ClearMessageAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }
}
