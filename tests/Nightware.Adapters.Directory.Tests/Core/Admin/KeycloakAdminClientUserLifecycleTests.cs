using System.Text.Json.Nodes;

using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Domain.Kernel;

using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Directory.Tests.Core.Admin;

/// <summary>Request shapes of the user-lifecycle and realm-role calls.</summary>
public sealed class KeycloakAdminClientUserLifecycleTests : IDisposable
{
    private const string UserId = "5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11";
    private const string Password = "Tmp-7vQ2-pX9z-Lm4k-Rw8s";
    private readonly KeycloakStub _keycloak = new();

    public KeycloakAdminClientUserLifecycleTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdapterHost Host(int retries = 2) =>
        AdapterHost.CreateConsole(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = retries.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static JsonNode Body(RequestMessage request) => JsonNode.Parse(request.Body!)!;

    [Fact]
    public async Task Creates_a_user_and_reads_the_id_from_the_location()
    {
        _keycloak.GivenAdmin("/users", 201, method: "POST", location: $"{_keycloak.Url}/admin/realms/nightware/users/{UserId}");
        await using AdapterHost host = Host();

        string id = (await host.Get<IKeycloakAdminClient>().CreateUserAsync(
            new KeycloakNewUser { Username = "carol", Email = "carol@acme.example", FirstName = "Carol", RequiredActions = ["UPDATE_PASSWORD", "CONFIGURE_TOTP"] }, Ct)).Value;

        id.ShouldBe(UserId);
        JsonObject body = Body(_keycloak.Admin("POST", "/users").Single()).AsObject();
        body["username"]!.GetValue<string>().ShouldBe("carol");
        body["email"]!.GetValue<string>().ShouldBe("carol@acme.example");
        body["firstName"]!.GetValue<string>().ShouldBe("Carol");
        body.ContainsKey("lastName").ShouldBeFalse();
        body["enabled"]!.GetValue<bool>().ShouldBeTrue();
        body["emailVerified"]!.GetValue<bool>().ShouldBeTrue();
        body["requiredActions"]!.AsArray().Select(static a => a!.GetValue<string>()).ShouldBe(["UPDATE_PASSWORD", "CONFIGURE_TOTP"]);
    }

    [Fact]
    public async Task Creating_a_taken_user_is_conflict_and_not_retried()
    {
        _keycloak.GivenAdmin("/users", 409, Fixture.Load("admin-error-conflict.json"), method: "POST");
        await using AdapterHost host = Host(retries: 3);

        Result<string> result = await host.Get<IKeycloakAdminClient>().CreateUserAsync(new KeycloakNewUser { Username = "alice", Email = "alice@nightware.localhost" }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        _keycloak.Admin("POST", "/users").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Logs_a_user_out_of_every_session()
    {
        _keycloak.GivenAdmin($"/users/{UserId}/logout", 204, method: "POST");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().LogoutUserAsync(UserId, Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("POST", $"/users/{UserId}/logout").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Lists_a_users_credentials_in_keycloaks_order()
    {
        _keycloak.GivenAdmin($"/users/{UserId}/credentials", 200,
            """[{"id":"c-pw","type":"password","createdDate":1700000000000},{"id":"c-otp","type":"otp","createdDate":1700000100000,"credentialData":"{}"}]""");
        await using AdapterHost host = Host();

        IReadOnlyList<KeycloakCredential> credentials = (await host.Get<IKeycloakAdminClient>().ListUserCredentialsAsync(UserId, Ct)).Value;

        credentials.Select(c => (c.Id, c.Type, c.CreatedDate)).ShouldBe([("c-pw", "password", 1700000000000L), ("c-otp", "otp", 1700000100000L)]);
    }

    [Fact]
    public async Task Moves_a_credential_to_first()
    {
        _keycloak.GivenAdmin($"/users/{UserId}/credentials/c-key/moveToFirst", 204, method: "POST");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().MoveCredentialToFirstAsync(UserId, "c-key", Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("POST", $"/users/{UserId}/credentials/c-key/moveToFirst").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sends_the_execute_actions_email_with_client_and_lifespan_in_seconds()
    {
        _keycloak.GivenAdmin($"/users/{UserId}/execute-actions-email", 204, method: "PUT");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().ExecuteActionsEmailAsync(UserId, ["UPDATE_PASSWORD", "CONFIGURE_TOTP"], "nightware-admin", TimeSpan.FromDays(3), Ct))
            .IsSuccess.ShouldBeTrue();

        RequestMessage put = _keycloak.Admin("PUT", $"/users/{UserId}/execute-actions-email").Single();
        put.Query!["client_id"].ShouldBe(["nightware-admin"]);
        put.Query["lifespan"].ShouldBe(["259200"]);
        put.Query.ShouldNotContainKey("redirect_uri");
        put.Body.ShouldBe("""["UPDATE_PASSWORD","CONFIGURE_TOTP"]""");
        put.Headers!["Content-Type"].Single().ShouldStartWith("application/json");
    }

    [Fact]
    public async Task Execute_actions_email_is_not_retried()
    {
        _keycloak.GivenAdmin($"/users/{UserId}/execute-actions-email", 500, """{"errorMessage":"Failed to send execute actions email"}""", method: "PUT");
        await using AdapterHost host = Host(retries: 3);

        (await host.Get<IKeycloakAdminClient>().ExecuteActionsEmailAsync(UserId, ["UPDATE_PASSWORD"], "nightware-admin", TimeSpan.FromHours(1), Ct))
            .Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _keycloak.Admin("PUT", $"/users/{UserId}/execute-actions-email").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Resets_a_temporary_password()
    {
        _keycloak.GivenAdmin($"/users/{UserId}/reset-password", 204, method: "PUT");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().ResetPasswordAsync(UserId, Password, temporary: true, Ct)).IsSuccess.ShouldBeTrue();

        JsonObject body = Body(_keycloak.Admin("PUT", $"/users/{UserId}/reset-password").Single()).AsObject();
        body["type"]!.GetValue<string>().ShouldBe("password");
        body["value"]!.GetValue<string>().ShouldBe(Password);
        body["temporary"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Theory]
    [InlineData(400)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task A_rejected_password_never_reaches_the_error_or_the_logs(int status)
    {
        // A server (or proxy) that echoes the request value back must not leak it.
        _keycloak.GivenAdmin($"/users/{UserId}/reset-password", status, $$"""{"errorMessage":"invalidPasswordHistoryMessage: {{Password}}"}""", method: "PUT");
        await using AdapterHost host = Host(retries: 0);

        Result result = await host.Get<IKeycloakAdminClient>().ResetPasswordAsync(UserId, Password, temporary: true, Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Message.ShouldNotContain(Password);
        result.Error.ToString().ShouldNotContain(Password);
        host.Logs.AllText.ShouldNotContain(Password);
        host.Logs.AllText.ShouldContain("reset-password"); // the failure itself is logged
    }

    [Fact]
    public async Task Reset_password_after_a_401_refresh_still_logs_no_password()
    {
        _keycloak.Server.Given(Request.Create().WithPath($"{KeycloakStub.AdminPath}/users/{UserId}/reset-password").UsingPut().WithHeader("Authorization", "Bearer sa-token-1"))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(401));
        _keycloak.GivenAdmin($"/users/{UserId}/reset-password", 204, method: "PUT");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakAdminClient>().ResetPasswordAsync(UserId, Password, temporary: true, Ct)).IsSuccess.ShouldBeTrue();

        IReadOnlyList<RequestMessage> puts = _keycloak.Admin("PUT", $"/users/{UserId}/reset-password");
        puts.Count.ShouldBe(2);
        puts.ShouldAllBe(static p => p.Body!.Contains(Password, StringComparison.Ordinal)); // the body is rebuilt for the retry
        host.Logs.AllText.ShouldNotContain(Password);
    }

    [Fact]
    public async Task Gets_a_realm_role_and_a_missing_one_names_it()
    {
        _keycloak.GivenAdmin("/roles/nightware-global-admin", 200, """{"id":"r-global","name":"nightware-global-admin","composite":false,"clientRole":false}""");
        _keycloak.GivenAdmin("/roles/nope", 404, """{"error":"Could not find role"}""");
        await using AdapterHost host = Host();
        IKeycloakAdminClient client = host.Get<IKeycloakAdminClient>();

        KeycloakRole role = (await client.GetRealmRoleAsync("nightware-global-admin", Ct)).Value;
        Result<KeycloakRole> missing = await client.GetRealmRoleAsync("nope", Ct);

        role.ShouldBe(new KeycloakRole { Id = "r-global", Name = "nightware-global-admin" });
        missing.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        missing.Error.Message.ShouldContain("'nope'");
    }

    [Fact]
    public async Task Lists_role_users_with_full_representations_across_pages()
    {
        string users = new JsonArray([.. Enumerable.Range(0, 100).Select(static i => (JsonNode)new JsonObject { ["id"] = $"u{i}" })]).ToJsonString();
        _keycloak.GivenAdmin("/roles/nightware-global-admin/users", 200, users, query: new Dictionary<string, string> { ["first"] = "0" });
        _keycloak.GivenAdmin("/roles/nightware-global-admin/users", 200, """[{"id":"last"}]""", query: new Dictionary<string, string> { ["first"] = "100" });
        await using AdapterHost host = Host();

        IReadOnlyList<KeycloakUser> admins = (await host.Get<IKeycloakAdminClient>().ListRealmRoleUsersAsync("nightware-global-admin", Ct)).Value;

        admins.Count.ShouldBe(101);
        admins[^1].Id.ShouldBe("last");
        _keycloak.Admin("GET", "/roles/nightware-global-admin/users")
            .ShouldAllBe(static r => r.Query!["briefRepresentation"].Single() == "false" && r.Query["max"].Single() == "100");
    }

    [Fact]
    public async Task Reads_direct_and_effective_realm_roles()
    {
        _keycloak.GivenAdmin($"/users/{UserId}/role-mappings/realm", 200, """[{"id":"r1","name":"default-roles-nightware"}]""");
        _keycloak.GivenAdmin($"/users/{UserId}/role-mappings/realm/composite", 200, """[{"id":"r1","name":"default-roles-nightware"},{"id":"r2","name":"offline_access"}]""");
        await using AdapterHost host = Host();
        IKeycloakAdminClient client = host.Get<IKeycloakAdminClient>();

        (await client.GetUserRealmRolesAsync(UserId, Ct)).Value.Select(static r => r.Name).ShouldBe(["default-roles-nightware"]);
        (await client.GetEffectiveUserRealmRolesAsync(UserId, Ct)).Value.Select(static r => r.Name).ShouldBe(["default-roles-nightware", "offline_access"]);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task Adds_and_removes_realm_role_mappings_with_the_role_in_the_body(string method)
    {
        _keycloak.GivenAdmin($"/users/{UserId}/role-mappings/realm", 204, method: method);
        await using AdapterHost host = Host();
        IKeycloakAdminClient client = host.Get<IKeycloakAdminClient>();
        KeycloakRole[] roles = [new KeycloakRole { Id = "r-global", Name = "nightware-global-admin" }];

        Result result = method == "POST"
            ? await client.AddUserRealmRolesAsync(UserId, roles, Ct)
            : await client.RemoveUserRealmRolesAsync(UserId, roles, Ct);

        result.IsSuccess.ShouldBeTrue();
        _keycloak.Admin(method, $"/users/{UserId}/role-mappings/realm").Single().Body.ShouldBe("""[{"id":"r-global","name":"nightware-global-admin"}]""");
    }

    [Fact]
    public async Task A_user_update_that_changes_nothing_never_puts()
    {
        _keycloak.GivenAdmin($"/users/{UserId}", 200, Fixture.Load("user-alice.json"));
        await using AdapterHost host = Host();

        KeycloakUser user = (await host.Get<IKeycloakAdminClient>().UpdateUserAsync(UserId, static _ => false, Ct)).Value;

        user.Username.ShouldBe("alice");
        _keycloak.Admin("PUT", $"/users/{UserId}").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_user_update_puts_the_whole_mutated_representation()
    {
        _keycloak.GivenAdmin($"/users/{UserId}", 200, Fixture.Load("user-alice.json"));
        _keycloak.GivenAdmin($"/users/{UserId}", 204, method: "PUT");
        await using AdapterHost host = Host();

        KeycloakUser user = (await host.Get<IKeycloakAdminClient>().UpdateUserAsync(
            UserId,
            static u =>
            {
                u["enabled"] = false;
                return true;
            },
            Ct)).Value;

        user.Enabled.ShouldBeFalse();
        JsonObject body = Body(_keycloak.Admin("PUT", $"/users/{UserId}").Single()).AsObject();
        body["enabled"]!.GetValue<bool>().ShouldBeFalse();
        body["attributes"]!["zoneinfo"]![0]!.GetValue<string>().ShouldBe("Australia/Sydney");
        body["access"].ShouldNotBeNull();
    }
}
