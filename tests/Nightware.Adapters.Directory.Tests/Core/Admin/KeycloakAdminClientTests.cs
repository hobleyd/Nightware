using System.Net;
using System.Text.Json.Nodes;

using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Domain.Kernel;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Directory.Tests.Core.Admin;

public sealed class KeycloakAdminClientTests : IDisposable
{
    private const string AliceId = "5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11";
    private readonly KeycloakStub _keycloak = new();

    public KeycloakAdminClientTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private AdapterHost Host(int retries = 2) =>
        AdapterHost.Create(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = retries.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private void GivenAdmin(string path, int status, string? body = null, string method = "GET", int priority = 10) =>
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + path).UsingMethod(method))
            .AtPriority(priority)
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body ?? string.Empty));

    private ILogEntry SingleAdminRequest() => _keycloak.AdminRequests.ShouldHaveSingleItem();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Gets_a_user_by_id_with_the_service_account_bearer()
    {
        GivenAdmin($"/users/{AliceId}", 200, Fixture.Load("user-alice.json"));
        await using AdapterHost host = Host();

        KeycloakUser user = (await host.Get<IKeycloakAdminClient>().GetUserAsync(AliceId, Ct)).Value;

        user.Id.ShouldBe(AliceId);
        user.Username.ShouldBe("alice");
        user.Email.ShouldBe("alice@nightware.localhost");
        user.FirstName.ShouldBe("Alice");
        user.LastName.ShouldBe("Anderson");
        user.Enabled.ShouldBeTrue();
        user.EmailVerified.ShouldBeTrue();
        user.CreatedTimestamp.ShouldBe(1794690000123);
        user.Attribute("zoneinfo").ShouldBe("Australia/Sydney");
        user.Attribute("missing").ShouldBeNull();
        SingleAdminRequest().RequestMessage!.Headers!["Authorization"].ShouldHaveSingleItem().ShouldBe("Bearer sa-token-1");
    }

    [Fact]
    public async Task User_id_is_escaped_in_the_path()
    {
        GivenAdmin("/users/a%2Fb", 404);
        await using AdapterHost host = Host();

        await host.Get<IKeycloakAdminClient>().GetUserAsync("a/b", Ct);

        SingleAdminRequest().RequestMessage!.Url.ShouldEndWith("/admin/realms/nightware/users/a%2Fb");
    }

    [Fact]
    public async Task Finds_users_with_search_and_paging()
    {
        GivenAdmin("/users", 200, Fixture.Load("users-search.json"));
        await using AdapterHost host = Host();

        IReadOnlyList<KeycloakUser> users = (await host.Get<IKeycloakAdminClient>().FindUsersAsync(
            new KeycloakUserQuery { Search = "*an*", First = 20, Max = 10, BriefRepresentation = false, Enabled = true }, Ct)).Value;

        users.Select(static u => u.Username).ShouldBe(["alice", "bob"]);
        users[0].Attributes.ShouldBeNull();
        users[1].Attribute("locale").ShouldBe("de");
        IDictionary<string, WireMock.Types.WireMockList<string>> query = SingleAdminRequest().RequestMessage!.Query!;
        query["search"].ShouldBe(["*an*"]);
        query["first"].ShouldBe(["20"]);
        query["max"].ShouldBe(["10"]);
        query["briefRepresentation"].ShouldBe(["false"]);
        query["enabled"].ShouldBe(["true"]);
        query.ShouldNotContainKey("exact");
    }

    [Fact]
    public async Task Attribute_query_is_sent_as_q()
    {
        GivenAdmin("/users", 200, "[]");
        await using AdapterHost host = Host();

        await host.Get<IKeycloakAdminClient>().FindUsersAsync(
            new KeycloakUserQuery { Attributes = new Dictionary<string, string> { ["locale"] = "en", ["zoneinfo"] = "UTC" } }, Ct);

        SingleAdminRequest().RequestMessage!.Query!["q"].ShouldBe(["locale:en zoneinfo:UTC"]);
    }

    [Fact]
    public async Task Finds_a_user_by_exact_email()
    {
        GivenAdmin("/users", 200, $"[{Fixture.Load("user-alice.json")}]");
        await using AdapterHost host = Host();

        KeycloakUser user = (await host.Get<IKeycloakAdminClient>().FindUserByEmailAsync("Alice@Nightware.localhost", Ct)).Value;

        user.Id.ShouldBe(AliceId);
        IDictionary<string, WireMock.Types.WireMockList<string>> query = SingleAdminRequest().RequestMessage!.Query!;
        query["email"].ShouldBe(["Alice@Nightware.localhost"]);
        query["exact"].ShouldBe(["true"]);
        query.ShouldNotContainKey("first");
    }

    [Fact]
    public async Task Finds_a_user_by_exact_username()
    {
        GivenAdmin("/users", 200, Fixture.Load("users-search.json"));
        await using AdapterHost host = Host();

        KeycloakUser user = (await host.Get<IKeycloakAdminClient>().FindUserByUsernameAsync("bob", Ct)).Value;

        user.Username.ShouldBe("bob");
        SingleAdminRequest().RequestMessage!.Query!["username"].ShouldBe(["bob"]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"id":"1","email":"other@nightware.localhost"}]""")]
    [InlineData("""[{"id":"1","email":"x@y"},{"id":"2","email":"x@y"}]""")]
    public async Task Email_lookup_is_not_found_unless_exactly_one_user_matches(string body)
    {
        GivenAdmin("/users", 200, body);
        await using AdapterHost host = Host();

        Result<KeycloakUser> user = await host.Get<IKeycloakAdminClient>().FindUserByEmailAsync("x@y", Ct);

        user.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Counts_users_without_paging_parameters()
    {
        GivenAdmin("/users/count", 200, "2");
        await using AdapterHost host = Host();

        int count = (await host.Get<IKeycloakAdminClient>().CountUsersAsync(new KeycloakUserQuery { Search = "a", First = 5, Max = 5 }, Ct)).Value;

        count.ShouldBe(2);
        IDictionary<string, WireMock.Types.WireMockList<string>> query = SingleAdminRequest().RequestMessage!.Query!;
        query["search"].ShouldBe(["a"]);
        query.ShouldNotContainKey("first");
        query.ShouldNotContainKey("max");
    }

    [Fact]
    public async Task Gets_user_attributes_or_an_empty_set()
    {
        GivenAdmin($"/users/{AliceId}", 200, Fixture.Load("user-alice.json"));
        GivenAdmin("/users/no-attrs", 200, """{"id":"no-attrs","username":"carol","enabled":true}""");
        await using AdapterHost host = Host();
        IKeycloakAdminClient client = host.Get<IKeycloakAdminClient>();

        IReadOnlyDictionary<string, IReadOnlyList<string>> alice = (await client.GetUserAttributesAsync(AliceId, Ct)).Value;
        IReadOnlyDictionary<string, IReadOnlyList<string>> carol = (await client.GetUserAttributesAsync("no-attrs", Ct)).Value;

        alice["locale"].ShouldBe(["en"]);
        alice["zoneinfo"].ShouldBe(["Australia/Sydney"]);
        carol.ShouldBeEmpty();
    }

    [Fact]
    public async Task Updates_attributes_by_read_modify_write_preserving_everything_else()
    {
        GivenAdmin($"/users/{AliceId}", 200, Fixture.Load("user-alice.json"));
        GivenAdmin($"/users/{AliceId}", 204, method: "PUT");
        await using AdapterHost host = Host();

        KeycloakUser updated = (await host.Get<IKeycloakAdminClient>().UpdateUserAttributesAsync(
            AliceId,
            new Dictionary<string, IReadOnlyList<string>?>
            {
                ["zoneinfo"] = ["Europe/Berlin"],
                ["locale"] = null,
                ["nightware.workingHours"] = ["""{"days":["monday"]}"""],
            },
            Ct)).Value;

        updated.Attribute("zoneinfo").ShouldBe("Europe/Berlin");
        updated.Attributes!.ShouldNotContainKey("locale");

        ILogEntry put = _keycloak.AdminRequests.Single(static e => e.RequestMessage!.Method == "PUT");
        put.RequestMessage!.Headers!["Content-Type"].Single().ShouldStartWith("application/json");
        JsonObject body = JsonNode.Parse(put.RequestMessage.Body!)!.AsObject();
        JsonObject attributes = body["attributes"]!.AsObject();
        attributes.Select(static kv => kv.Key).ShouldBe(["zoneinfo", "nightware.workingHours"], ignoreOrder: true);
        attributes["zoneinfo"]![0]!.GetValue<string>().ShouldBe("Europe/Berlin");
        attributes["nightware.workingHours"]![0]!.GetValue<string>().ShouldBe("""{"days":["monday"]}""");
        body["email"]!.GetValue<string>().ShouldBe("alice@nightware.localhost");
        body["firstName"]!.GetValue<string>().ShouldBe("Alice");
        body["requiredActions"].ShouldNotBeNull();
        body["access"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Attribute_update_creates_the_attributes_object_when_missing()
    {
        GivenAdmin("/users/u1", 200, """{"id":"u1","username":"carol","enabled":true}""");
        GivenAdmin("/users/u1", 204, method: "PUT");
        await using AdapterHost host = Host();

        await host.Get<IKeycloakAdminClient>().UpdateUserAttributesAsync("u1", new Dictionary<string, IReadOnlyList<string>?> { ["locale"] = ["fr"] }, Ct);

        JsonObject body = JsonNode.Parse(_keycloak.AdminRequests.Single(static e => e.RequestMessage!.Method == "PUT").RequestMessage!.Body!)!.AsObject();
        body["attributes"]!.ToJsonString().ShouldBe("""{"locale":["fr"]}""");
    }

    [Fact]
    public async Task Attribute_update_of_a_missing_user_is_not_found_and_never_puts()
    {
        GivenAdmin("/users/ghost", 404, """{"error":"User not found"}""");
        await using AdapterHost host = Host();

        Result<KeycloakUser> result = await host.Get<IKeycloakAdminClient>().UpdateUserAttributesAsync("ghost", new Dictionary<string, IReadOnlyList<string>?>(), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _keycloak.AdminRequests.ShouldNotContain(static e => e.RequestMessage!.Method == "PUT");
    }

    [Fact]
    public async Task Attribute_update_is_not_retried_on_a_transient_failure()
    {
        GivenAdmin("/users/u1", 200, """{"id":"u1"}""");
        GivenAdmin("/users/u1", 503, method: "PUT");
        await using AdapterHost host = Host(retries: 3);

        Result<KeycloakUser> result = await host.Get<IKeycloakAdminClient>().UpdateUserAttributesAsync("u1", new Dictionary<string, IReadOnlyList<string>?> { ["a"] = ["b"] }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _keycloak.AdminRequests.Count(static e => e.RequestMessage!.Method == "PUT").ShouldBe(1);
    }

    [Fact]
    public async Task Gets_are_retried_on_a_transient_failure()
    {
        _keycloak.Server.Given(Request.Create().WithPath($"{KeycloakStub.AdminPath}/users/{AliceId}").UsingGet())
            .InScenario("flaky").WillSetStateTo("ok").AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(502));
        GivenAdmin($"/users/{AliceId}", 200, Fixture.Load("user-alice.json"));
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().GetUserAsync(AliceId, Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.AdminRequests.Count().ShouldBe(2);
    }

    [Fact]
    public async Task A_401_refreshes_the_service_account_token_and_retries_once()
    {
        _keycloak.Server.Given(Request.Create().WithPath($"{KeycloakStub.AdminPath}/users/{AliceId}").UsingGet().WithHeader("Authorization", "Bearer sa-token-1"))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("""{"error":"HTTP 401 Unauthorized"}"""));
        GivenAdmin($"/users/{AliceId}", 200, Fixture.Load("user-alice.json"));
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().GetUserAsync(AliceId, Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.ClientCredentialsHits.ShouldBe(2);
        _keycloak.AdminRequests.Last().RequestMessage!.Headers!["Authorization"].Single().ShouldBe("Bearer sa-token-2");
    }

    [Fact]
    public async Task A_second_401_is_backend_unavailable_never_backend_auth_failed()
    {
        GivenAdmin($"/users/{AliceId}", 401, """{"error":"HTTP 401 Unauthorized"}""");
        await using AdapterHost host = Host();

        Result<KeycloakUser> result = await host.Get<IKeycloakAdminClient>().GetUserAsync(AliceId, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _keycloak.AdminRequests.Count().ShouldBe(2);
    }

    [Theory]
    [InlineData(400, "admin-error-validation.json", ErrorKind.Validation)]
    [InlineData(403, null, ErrorKind.Forbidden)]
    [InlineData(404, null, ErrorKind.NotFound)]
    [InlineData(409, "admin-error-conflict.json", ErrorKind.Conflict)]
    [InlineData(429, null, ErrorKind.Throttled)]
    [InlineData(500, null, ErrorKind.BackendUnavailable)]
    [InlineData(418, null, ErrorKind.Unknown)]
    public async Task Maps_admin_errors(int status, string? fixture, ErrorKind expected)
    {
        GivenAdmin($"/users/{AliceId}", status, fixture is null ? null : Fixture.Load(fixture));
        await using AdapterHost host = Host(retries: 0);

        Result<KeycloakUser> result = await host.Get<IKeycloakAdminClient>().GetUserAsync(AliceId, Ct);

        result.Error!.Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task Error_message_carries_keycloaks_reason()
    {
        GivenAdmin($"/users/{AliceId}", 409, Fixture.Load("admin-error-conflict.json"));
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().GetUserAsync(AliceId, Ct)).Error!.Message.ShouldContain("User exists with same email");
    }

    [Fact]
    public async Task Throttling_carries_retry_after()
    {
        _keycloak.Server.Given(Request.Create().WithPath($"{KeycloakStub.AdminPath}/users").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "12"));
        await using AdapterHost host = Host(retries: 0);

        Result<IReadOnlyList<KeycloakUser>> result = await host.Get<IKeycloakAdminClient>().FindUsersAsync(new KeycloakUserQuery(), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Throttled);
        result.Error.RetryAfterSeconds.ShouldBe(12);
    }

    [Fact]
    public async Task Malformed_body_is_backend_unavailable()
    {
        GivenAdmin($"/users/{AliceId}", 200, "<html>proxy error</html>");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().GetUserAsync(AliceId, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Service_account_failure_stops_before_the_admin_call()
    {
        _keycloak.GivenServiceAccountTokenFails(HttpStatusCode.Unauthorized, Fixture.Load("error-unauthorized-client.json"));
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().GetUserAsync(AliceId, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _keycloak.AdminRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unreachable_admin_api_is_backend_unavailable()
    {
        using KeycloakStub tokenOnly = new();
        tokenOnly.GivenServiceAccountToken();
        tokenOnly.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + "/*").UsingAnyMethod())
            .RespondWith(Response.Create().WithFault(WireMock.ResponseBuilders.FaultType.MALFORMED_RESPONSE_CHUNK));
        await using AdapterHost host = AdapterHost.Create(tokenOnly.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = "0" });

        (await host.Get<IKeycloakAdminClient>().GetUserAsync(AliceId, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }
}
