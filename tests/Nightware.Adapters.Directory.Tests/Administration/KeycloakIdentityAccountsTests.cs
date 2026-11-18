using System.Text.Json.Nodes;

using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

using WireMock;

namespace Nightware.Adapters.Directory.Tests.Administration;

public sealed class KeycloakIdentityAccountsTests : IDisposable
{
    private const string UserId = "5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11";
    private const string UserPath = $"/users/{UserId}";
    private const string GlobalRole = """{"id":"r-global","name":"nightware-global-admin"}""";
    private readonly KeycloakStub _keycloak = new();

    public KeycloakIdentityAccountsTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MailAddress Address(string value) => MailAddress.TryParse(value, out MailAddress? a) ? a : throw new ArgumentException(value);

    private AdapterHost Host() => AdapterHost.CreateConsole(_keycloak.Url);

    private void GivenAliceWithPut()
    {
        _keycloak.GivenAdmin(UserPath, 200, Fixture.Load("user-alice.json"));
        _keycloak.GivenAdmin(UserPath, 204, method: "PUT");
    }

    private JsonObject PutBody() => JsonNode.Parse(_keycloak.Admin("PUT", UserPath).Single().Body!)!.AsObject();

    [Fact]
    public async Task Gets_an_identity()
    {
        _keycloak.GivenAdmin(UserPath, 200, Fixture.Load("user-alice.json"));
        await using AdapterHost host = Host();

        IdentityAccount alice = (await host.Scoped<IIdentityAccounts>().GetAsync(UserId, Ct)).Value;

        alice.ShouldBe(new IdentityAccount(UserId, "alice", "alice@nightware.localhost", true) { FirstName = "Alice", LastName = "Anderson", Aliases = alice.Aliases });
        alice.Aliases.ShouldBeEmpty();
        alice.DisplayName.ShouldBe("Alice Anderson");
    }

    [Fact]
    public async Task A_missing_identity_is_not_found()
    {
        _keycloak.GivenAdmin(UserPath, 404);
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().GetAsync(UserId, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Finds_by_exact_email_and_username()
    {
        _keycloak.GivenAdmin("/users", 200, $"[{Fixture.Load("user-alice.json")}]");
        await using AdapterHost host = Host();
        IIdentityAccounts accounts = host.Scoped<IIdentityAccounts>();

        (await accounts.FindByEmailAsync("alice@nightware.localhost", Ct)).Value.Id.ShouldBe(UserId);
        (await accounts.FindByUsernameAsync("alice", Ct)).Value.Id.ShouldBe(UserId);
        (await accounts.FindByUsernameAsync("bob", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        IReadOnlyList<RequestMessage> searches = _keycloak.Admin("GET", "/users");
        searches[0].Query!["email"].ShouldBe(["alice@nightware.localhost"]);
        searches[1].Query!["username"].ShouldBe(["alice"]);
        searches.ShouldAllBe(static s => s.Query!["exact"].Single() == "true");
    }

    [Fact]
    public async Task Creates_an_enabled_verified_identity_with_required_actions()
    {
        _keycloak.GivenAdmin("/users", 201, method: "POST", location: $"{_keycloak.Url}/admin/realms/nightware/users/new-id");
        await using AdapterHost host = Host();

        IdentityAccount created = (await host.Scoped<IIdentityAccounts>().CreateAsync(
            new NewIdentity("Carol", Address("carol@acme.example"), " Carol ", null, [RequiredAction.UpdatePassword, RequiredAction.ConfigureTotp, RequiredAction.UpdatePassword]),
            Ct)).Value;

        created.ShouldBe(new IdentityAccount("new-id", "carol", "carol@acme.example", true) { FirstName = "Carol" });
        JsonObject body = JsonNode.Parse(_keycloak.Admin("POST", "/users").Single().Body!)!.AsObject();
        body["username"]!.GetValue<string>().ShouldBe("Carol");
        body["email"]!.GetValue<string>().ShouldBe("carol@acme.example");
        body["firstName"]!.GetValue<string>().ShouldBe("Carol");
        body.ContainsKey("lastName").ShouldBeFalse();
        body["enabled"]!.GetValue<bool>().ShouldBeTrue();
        body["emailVerified"]!.GetValue<bool>().ShouldBeTrue();
        body["requiredActions"]!.AsArray().Select(static a => a!.GetValue<string>()).ShouldBe(["UPDATE_PASSWORD", "CONFIGURE_TOTP"]);
    }

    [Fact]
    public async Task Creating_a_taken_identity_is_conflict()
    {
        _keycloak.GivenAdmin("/users", 409, Fixture.Load("admin-error-conflict.json"), method: "POST");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().CreateAsync(new NewIdentity("alice", Address("alice@nightware.localhost"), null, null, []), Ct))
            .Error!.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public async Task Sets_the_name_keeping_everything_else()
    {
        GivenAliceWithPut();
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().SetNameAsync(UserId, "Alicia", null, Ct)).IsSuccess.ShouldBeTrue();

        JsonObject body = PutBody();
        body["firstName"]!.GetValue<string>().ShouldBe("Alicia");
        body["lastName"]!.GetValue<string>().ShouldBeEmpty(); // cleared
        body["email"]!.GetValue<string>().ShouldBe("alice@nightware.localhost");
        body["attributes"]!["zoneinfo"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Setting_the_same_name_writes_nothing()
    {
        GivenAliceWithPut();
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().SetNameAsync(UserId, "Alice", "Anderson", Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("PUT", UserPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task Sets_aliases_as_a_multivalued_attribute()
    {
        GivenAliceWithPut();
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().SetAliasesAsync(UserId, [Address("a@nightware.localhost"), Address("A@nightware.localhost"), Address("info@nightware.localhost")], Ct))
            .IsSuccess.ShouldBeTrue();

        JsonObject attributes = PutBody()["attributes"]!.AsObject();
        attributes["nightware_aliases"]!.AsArray().Select(static a => a!.GetValue<string>()).ShouldBe(["a@nightware.localhost", "info@nightware.localhost"]);
        attributes["locale"].ShouldNotBeNull();
    }

    [Fact]
    public async Task No_aliases_removes_the_attribute()
    {
        _keycloak.GivenAdmin(UserPath, 200, """{"id":"5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11","attributes":{"nightware_aliases":["x@y.example"],"locale":["en"]}}""");
        _keycloak.GivenAdmin(UserPath, 204, method: "PUT");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().SetAliasesAsync(UserId, [], Ct)).IsSuccess.ShouldBeTrue();

        PutBody()["attributes"]!.AsObject().Select(static kv => kv.Key).ShouldBe(["locale"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sets_enabled_only_when_it_changes(bool enabled)
    {
        GivenAliceWithPut(); // alice is enabled
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().SetEnabledAsync(UserId, enabled, Ct)).IsSuccess.ShouldBeTrue();

        if (enabled)
        {
            _keycloak.Admin("PUT", UserPath).ShouldBeEmpty();
        }
        else
        {
            PutBody()["enabled"]!.GetValue<bool>().ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Ends_sessions()
    {
        _keycloak.GivenAdmin($"{UserPath}/logout", 204, method: "POST");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().EndSessionsAsync(UserId, Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("POST", $"{UserPath}/logout").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Ranks_the_newest_passkey_first()
    {
        _keycloak.GivenAdmin($"{UserPath}/credentials", 200,
            """[{"id":"c-pw","type":"password","createdDate":1},{"id":"c-otp","type":"otp","createdDate":2},{"id":"c-old-key","type":"webauthn","createdDate":3},{"id":"c-new-key","type":"webauthn","createdDate":4}]""");
        _keycloak.GivenAdmin($"{UserPath}/credentials/c-new-key/moveToFirst", 204, method: "POST");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().PreferNewestPasskeyAsync(UserId, Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("POST", $"{UserPath}/credentials/c-new-key/moveToFirst").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_passkey_already_first_is_left_alone()
    {
        _keycloak.GivenAdmin($"{UserPath}/credentials", 200, """[{"id":"c-key","type":"webauthn","createdDate":3},{"id":"c-otp","type":"otp","createdDate":2}]""");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().PreferNewestPasskeyAsync(UserId, Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("POST", $"{UserPath}/credentials/c-key/moveToFirst").ShouldBeEmpty();
    }

    [Fact]
    public async Task Preferring_a_passkey_without_one_is_not_found()
    {
        _keycloak.GivenAdmin($"{UserPath}/credentials", 200, """[{"id":"c-otp","type":"otp","createdDate":2},{"id":"c-pw","type":"password","createdDate":1}]""");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().PreferNewestPasskeyAsync(UserId, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Sends_the_action_email_naming_the_consoles_own_client()
    {
        _keycloak.GivenAdmin($"{UserPath}/execute-actions-email", 204, method: "PUT");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().SendActionEmailAsync(UserId, [RequiredAction.UpdatePassword, RequiredAction.ConfigureTotp], TimeSpan.FromDays(3), Ct))
            .IsSuccess.ShouldBeTrue();

        RequestMessage put = _keycloak.Admin("PUT", $"{UserPath}/execute-actions-email").Single();
        put.Query!["client_id"].ShouldBe([AdapterHost.ConsoleClientId]);
        put.Query["lifespan"].ShouldBe(["259200"]);
        put.Body.ShouldBe("""["UPDATE_PASSWORD","CONFIGURE_TOTP"]""");
    }

    [Fact]
    public async Task An_action_email_needs_an_action_and_a_lifespan()
    {
        await using AdapterHost host = Host();
        IIdentityAccounts accounts = host.Scoped<IIdentityAccounts>();

        (await accounts.SendActionEmailAsync(UserId, [], TimeSpan.FromHours(1), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await accounts.SendActionEmailAsync(UserId, [RequiredAction.UpdatePassword], TimeSpan.Zero, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _keycloak.AdminRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sets_a_temporary_password_without_logging_it()
    {
        const string password = "Gen-9fK2-wQ7x-Zr4m-Tn6p";
        _keycloak.GivenAdmin($"{UserPath}/reset-password", 204, method: "PUT");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().SetTemporaryPasswordAsync(UserId, new TemporaryCredential(password), Ct)).IsSuccess.ShouldBeTrue();

        JsonObject body = JsonNode.Parse(_keycloak.Admin("PUT", $"{UserPath}/reset-password").Single().Body!)!.AsObject();
        body["value"]!.GetValue<string>().ShouldBe(password);
        body["temporary"]!.GetValue<bool>().ShouldBeTrue();
        host.Logs.AllText.ShouldNotContain(password);
    }

    [Fact]
    public async Task Adds_a_required_action_once()
    {
        _keycloak.GivenAdmin(UserPath, 200, """{"id":"5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11","requiredActions":["VERIFY_EMAIL"]}""");
        _keycloak.GivenAdmin(UserPath, 204, method: "PUT");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().AddRequiredActionAsync(UserId, RequiredAction.ConfigureTotp, Ct)).IsSuccess.ShouldBeTrue();

        PutBody()["requiredActions"]!.AsArray().Select(static a => a!.GetValue<string>()).ShouldBe(["VERIFY_EMAIL", "CONFIGURE_TOTP"]);
    }

    [Fact]
    public async Task Adding_a_present_required_action_writes_nothing()
    {
        _keycloak.GivenAdmin(UserPath, 200, """{"id":"5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11","requiredActions":["CONFIGURE_TOTP"]}""");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().AddRequiredActionAsync(UserId, RequiredAction.ConfigureTotp, Ct)).IsSuccess.ShouldBeTrue();
        _keycloak.Admin("PUT", UserPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task Adds_a_required_action_to_a_user_without_the_array()
    {
        _keycloak.GivenAdmin(UserPath, 200, """{"id":"5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11"}""");
        _keycloak.GivenAdmin(UserPath, 204, method: "PUT");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().AddRequiredActionAsync(UserId, RequiredAction.UpdatePassword, Ct)).IsSuccess.ShouldBeTrue();
        PutBody()["requiredActions"]!.ToJsonString().ShouldBe("""["UPDATE_PASSWORD"]""");
    }

    [Fact]
    public async Task Lists_global_admins_from_the_role()
    {
        _keycloak.GivenAdmin("/roles/nightware-global-admin/users", 200, Fixture.Load("organization-members.json"));
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().ListGlobalAdminsAsync(Ct)).Value.Select(static a => a.Username).ShouldBe(["alice", "bob"]);
        _keycloak.Admin("GET", "/roles/nightware-global-admin/users").Single().Query!["briefRepresentation"].ShouldBe(["false"]);
    }

    [Theory]
    [InlineData("""[{"id":"r1","name":"default-roles-nightware"},{"id":"r-global","name":"nightware-global-admin"}]""", true)]
    [InlineData("""[{"id":"r1","name":"default-roles-nightware"},{"id":"r2","name":"nightware-entity-admin"}]""", false)]
    public async Task Global_admin_is_read_from_effective_roles(string roles, bool expected)
    {
        _keycloak.GivenAdmin($"{UserPath}/role-mappings/realm/composite", 200, roles);
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().IsGlobalAdminAsync(UserId, Ct)).Value.ShouldBe(expected);
    }

    [Fact]
    public async Task Grants_global_admin_by_mapping_the_role()
    {
        _keycloak.GivenAdmin($"{UserPath}/role-mappings/realm", 200, """[{"id":"r1","name":"default-roles-nightware"}]""");
        _keycloak.GivenAdmin("/roles/nightware-global-admin", 200, GlobalRole);
        _keycloak.GivenAdmin($"{UserPath}/role-mappings/realm", 204, method: "POST");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().SetGlobalAdminAsync(UserId, true, Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.Admin("POST", $"{UserPath}/role-mappings/realm").Single().Body.ShouldBe("""[{"id":"r-global","name":"nightware-global-admin"}]""");
    }

    [Fact]
    public async Task Revokes_global_admin_by_unmapping_the_role()
    {
        _keycloak.GivenAdmin($"{UserPath}/role-mappings/realm", 200, $"[{GlobalRole}]");
        _keycloak.GivenAdmin("/roles/nightware-global-admin", 200, GlobalRole);
        _keycloak.GivenAdmin($"{UserPath}/role-mappings/realm", 204, method: "DELETE");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().SetGlobalAdminAsync(UserId, false, Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.Admin("DELETE", $"{UserPath}/role-mappings/realm").Single().Body.ShouldBe("""[{"id":"r-global","name":"nightware-global-admin"}]""");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Setting_global_admin_to_its_current_state_writes_nothing(bool granted)
    {
        _keycloak.GivenAdmin($"{UserPath}/role-mappings/realm", 200, granted ? $"[{GlobalRole}]" : "[]");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().SetGlobalAdminAsync(UserId, granted, Ct)).IsSuccess.ShouldBeTrue();

        _keycloak.AdminRequests.ShouldAllBe(static e => e.RequestMessage!.Method == "GET");
        _keycloak.Admin("GET", "/roles/nightware-global-admin").ShouldBeEmpty();
    }

    [Fact]
    public async Task Granting_a_role_the_realm_lacks_is_not_found()
    {
        _keycloak.GivenAdmin($"{UserPath}/role-mappings/realm", 200, "[]");
        _keycloak.GivenAdmin("/roles/nightware-global-admin", 404);
        await using AdapterHost host = Host();

        Result result = await host.Scoped<IIdentityAccounts>().SetGlobalAdminAsync(UserId, true, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        result.Error.Message.ShouldContain("nightware-global-admin");
    }

    [Fact]
    public async Task Writes_a_deletion_marker_in_utc_round_trip_format()
    {
        GivenAliceWithPut();
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().SetDeletionMarkerAsync(UserId, DeletionTarget.Mail, new DateTimeOffset(2026, 11, 17, 21, 0, 0, TimeSpan.FromHours(11)), Ct))
            .IsSuccess.ShouldBeTrue();

        JsonObject attributes = PutBody()["attributes"]!.AsObject();
        attributes["nightware_mail_deleted_at"]!.AsArray().Select(static v => v!.GetValue<string>()).ShouldBe(["2026-11-17T10:00:00.0000000+00:00"]);
        attributes["zoneinfo"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Clears_a_deletion_marker_with_null()
    {
        _keycloak.GivenAdmin(UserPath, 200, """{"id":"5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11","attributes":{"nightware_groupware_deleted_at":["2026-11-10T09:30:00.0000000+00:00"],"locale":["en"]}}""");
        _keycloak.GivenAdmin(UserPath, 204, method: "PUT");
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().SetDeletionMarkerAsync(UserId, DeletionTarget.GroupwareData, null, Ct)).IsSuccess.ShouldBeTrue();

        PutBody()["attributes"]!.AsObject().Select(static kv => kv.Key).ShouldBe(["locale"]);
    }

    [Fact]
    public async Task Written_markers_read_back_onto_the_identity()
    {
        _keycloak.GivenAdmin(UserPath, 200, """{"id":"5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11","username":"bob","email":"bob@x.example","attributes":{"nightware_mail_deleted_at":["2026-11-17T10:00:00.0000000+00:00"],"nightware_groupware_deleted_at":["2026-11-18T11:00:00.0000000+00:00"]}}""");
        await using AdapterHost host = Host();

        IdentityAccount bob = (await host.Scoped<IIdentityAccounts>().GetAsync(UserId, Ct)).Value;

        bob.MailDeletedAt.ShouldBe(new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero));
        bob.GroupwareDeletedAt.ShouldBe(new DateTimeOffset(2026, 11, 18, 11, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task An_access_event_goes_first_in_the_users_history_beside_their_other_attributes()
    {
        _keycloak.GivenAdmin(UserPath, 200, """{"id":"5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11","attributes":{"nightware_access_history":["2026-11-17T08:00:00Z|Offboarded|ops|Mail:Error"],"locale":["en"]}}""");
        _keycloak.GivenAdmin(UserPath, 204, method: "PUT");
        await using AdapterHost host = Host();
        var added = new AccessEvent(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero), AccessEventKind.OffboardingFinished, "ops", [new SystemState(BackendKind.Mail, LinkState.Disabled)]);

        (await host.Scoped<IIdentityAccounts>().AddAccessEventAsync(UserId, added, Ct)).IsSuccess.ShouldBeTrue();

        JsonObject attributes = PutBody()["attributes"]!.AsObject();
        attributes["nightware_access_history"]!.AsArray().Select(static v => v!.GetValue<string>()).ShouldBe(
            ["2026-11-18T09:00:00Z|OffboardingFinished|ops|Mail:Disabled", "2026-11-17T08:00:00Z|Offboarded|ops|Mail:Error"]);
        attributes["locale"].ShouldNotBeNull();
    }

    [Fact]
    public async Task The_history_reads_back_newest_first_skipping_lines_it_cannot_read()
    {
        _keycloak.GivenAdmin(UserPath, 200, """{"id":"5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11","username":"bob","email":"bob@x.example","attributes":{"nightware_access_history":["2026-11-17T08:00:00Z|Offboarded|ops|Mail:Error","garbage","2026-11-18T09:00:00Z|Reinstated|ops|"]}}""");
        await using AdapterHost host = Host();

        IdentityAccount bob = (await host.Scoped<IIdentityAccounts>().GetAsync(UserId, Ct)).Value;

        bob.AccessHistory.Select(static e => e.Kind).ShouldBe([AccessEventKind.Reinstated, AccessEventKind.Offboarded]);
    }

    [Fact]
    public async Task A_failing_read_never_writes()
    {
        _keycloak.GivenAdmin(UserPath, 403);
        await using AdapterHost host = Host();

        (await host.Scoped<IIdentityAccounts>().SetEnabledAsync(UserId, false, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _keycloak.Admin("PUT", UserPath).ShouldBeEmpty();
    }
}
