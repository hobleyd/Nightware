using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Directory.MailboxSettings;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Directory.Tests.MailboxSettings;

public sealed class KeycloakUserPreferencesStoreTests : IDisposable
{
    private const string AliceId = "5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11";
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");
    private readonly KeycloakStub _keycloak = new();

    public KeycloakUserPreferencesStoreTests() => _keycloak.GivenServiceAccountToken();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void GivenAdmin(string path, int status, string? body = null, string method = "GET") =>
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + path).UsingMethod(method))
            .AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body ?? string.Empty));

    private void GivenAlice(string fixture = "user-alice-preferences.json")
    {
        GivenAdmin("/users", 200, $"[{Fixture.Load(fixture)}]");
        GivenAdmin($"/users/{AliceId}", 200, Fixture.Load(fixture));
    }

    private static IUserPreferencesStore Store(AdapterHost host) => host.Provider.CreateScope().ServiceProvider.GetRequiredService<IUserPreferencesStore>();

    private AdapterHost Host() => AdapterHost.Create(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = "0" });

    [Fact]
    public async Task Is_registered_as_the_port()
    {
        await using var host = Host();

        Store(host).ShouldBeOfType<KeycloakUserPreferencesStore>();
    }

    [Fact]
    public async Task Reads_every_preference_from_the_users_attributes()
    {
        GivenAlice();
        await using var host = Host();

        var preferences = (await Store(host).GetAsync(Alice, Ct)).Value;

        preferences.TimeZone.ShouldBe("Australia/Sydney");
        preferences.Language.ShouldBe("en");
        preferences.DateFormat.ShouldBe("dd/MM/yyyy");
        preferences.TimeFormat.ShouldBe("h:mm tt");
        preferences.WorkingHours.ShouldNotBeNull();
        preferences.WorkingHours.DaysOfWeek.ShouldBe([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);
        preferences.WorkingHours.StartTime.ShouldBe(new TimeOnly(9, 0));
        preferences.WorkingHours.EndTime.ShouldBe(new TimeOnly(17, 30));
        preferences.WorkingHours.TimeZone.ShouldBe("Australia/Sydney");

        var lookup = _keycloak.AdminRequests.ShouldHaveSingleItem().RequestMessage!;
        lookup.Query!["email"].ShouldBe(["alice@nightware.localhost"]);
        lookup.Query["exact"].ShouldBe(["true"]);
    }

    [Fact]
    public async Task A_user_without_attributes_has_no_preferences()
    {
        GivenAdmin("/users", 200, """[{"id":"u1","username":"carol","email":"alice@nightware.localhost","enabled":true}]""");
        await using var host = Host();

        var preferences = (await Store(host).GetAsync(Alice, Ct)).Value;

        preferences.ShouldBe(new UserPreferences(null, null));
    }

    [Fact]
    public async Task An_unknown_mailbox_is_not_found()
    {
        GivenAdmin("/users", 200, "[]");
        await using var host = Host();

        var result = await Store(host).GetAsync(new MailboxRef("team@nightware.localhost"), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Admin_failures_pass_through()
    {
        GivenAdmin("/users", 403);
        await using var host = Host();

        var result = await Store(host).GetAsync(Alice, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Writes_only_the_patched_attributes()
    {
        GivenAlice();
        GivenAdmin($"/users/{AliceId}", 204, method: "PUT");
        await using var host = Host();
        var hours = new WorkingHours([DayOfWeek.Monday], new TimeOnly(8, 0), new TimeOnly(16, 0), "Europe/London");

        var preferences = (await Store(host).SetAsync(Alice, new UserPreferencesPatch { TimeZone = "Europe/London", WorkingHours = hours }, Ct)).Value;

        preferences.TimeZone.ShouldBe("Europe/London");
        preferences.Language.ShouldBe("en");
        preferences.WorkingHours.ShouldBe(hours with { DaysOfWeek = preferences.WorkingHours!.DaysOfWeek });
        preferences.WorkingHours.DaysOfWeek.ShouldBe([DayOfWeek.Monday]);
        var attributes = JsonNode.Parse(Put().RequestMessage!.Body!)!["attributes"]!.AsObject();
        attributes["zoneinfo"]![0]!.GetValue<string>().ShouldBe("Europe/London");
        attributes["locale"]![0]!.GetValue<string>().ShouldBe("en");
        attributes["nightware_working_hours"]![0]!.GetValue<string>()
            .ShouldBe("""{"daysOfWeek":["monday"],"startTime":"08:00:00.0000000","endTime":"16:00:00.0000000","timeZone":{"name":"Europe/London"}}""");
    }

    [Fact]
    public async Task An_empty_patch_writes_nothing()
    {
        GivenAlice();
        await using var host = Host();

        var preferences = (await Store(host).SetAsync(Alice, new UserPreferencesPatch(), Ct)).Value;

        preferences.TimeZone.ShouldBe("Australia/Sydney");
        _keycloak.AdminRequests.ShouldNotContain(static e => e.RequestMessage!.Method == "PUT");
    }

    [Fact]
    public async Task Writing_the_locale()
    {
        GivenAlice();
        GivenAdmin($"/users/{AliceId}", 204, method: "PUT");
        await using var host = Host();

        (await Store(host).SetAsync(Alice, new UserPreferencesPatch { Language = "fr" }, Ct)).Value.Language.ShouldBe("fr");
    }

    [Fact]
    public async Task Write_failures_pass_through()
    {
        GivenAlice();
        GivenAdmin($"/users/{AliceId}", 403, method: "PUT");
        await using var host = Host();

        var result = await Store(host).SetAsync(Alice, new UserPreferencesPatch { TimeZone = "UTC" }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Writing_for_an_unknown_mailbox_is_not_found()
    {
        GivenAdmin("/users", 200, "[]");
        await using var host = Host();

        var result = await Store(host).SetAsync(new MailboxRef("team@nightware.localhost"), new UserPreferencesPatch { TimeZone = "UTC" }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"daysOfWeek":["funday"],"startTime":"09:00:00","endTime":"17:00:00"}""")]
    [InlineData("""{"daysOfWeek":["monday"],"startTime":"nine","endTime":"17:00:00"}""")]
    [InlineData("""{"daysOfWeek":"monday","startTime":"09:00:00","endTime":"17:00:00"}""")]
    [InlineData("  ")]
    public void Unreadable_working_hours_count_as_unset(string json) =>
        KeycloakUserPreferencesStore.ReadWorkingHours(json).ShouldBeNull();

    [Fact]
    public void Working_hours_without_a_zone_are_utc() =>
        KeycloakUserPreferencesStore.ReadWorkingHours("""{"daysOfWeek":["sunday"],"startTime":"09:00:00","endTime":"17:00:00"}""")!.TimeZone.ShouldBe("UTC");

    private ILogEntry Put() => _keycloak.AdminRequests.Single(static e => e.RequestMessage!.Method == "PUT");

    public void Dispose() => _keycloak.Dispose();
}
