using System.Text.Json.Nodes;

using Nightware.Application.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Administration;

/// <summary><see cref="IGroupwareAccounts"/> against a WireMock Nextcloud, through the real registration.</summary>
public sealed class NextcloudGroupwareAccountsTests : IDisposable
{
    private const string UserPath = "/ocs/v1.php/cloud/users/alice";
    private const string PreProvisionPath = "/ocs/v2.php/apps/user_oidc/api/v1/user";
    private const string ProviderPath = "/ocs/v2.php/apps/user_oidc/api/v1/provider";

    // Shape of provisioning_api's getUserData (abridged), as an administrator sees it.
    private const string Alice = """
        {"enabled":true,"storageLocation":"/var/www/html/data/alice","id":"alice","firstLoginTimestamp":-1,"lastLoginTimestamp":0,
         "lastLogin":0,"backend":"user_oidc","subadmin":[],"quota":{"quota":"none","used":0},"manager":"","email":"alice@example.test",
         "additional_mail":[],"displayname":"Alice Liddell","display-name":"Alice Liddell","phone":"","address":"","website":"",
         "twitter":"","fediverse":"","organisation":"","role":"","headline":"","biography":"","profile_enabled":"1","pronouns":"",
         "groups":[],"language":"en","locale":"","notify_email":null,"backendCapabilities":{"setDisplayName":true,"setPassword":false}}
        """;

    private readonly NextcloudAdminServer _nextcloud = new();

    public void Dispose() => _nextcloud.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IGroupwareAccounts Accounts => _nextcloud.Get<IGroupwareAccounts>();

    private WireMock.RequestMessage Single => (WireMock.RequestMessage)_nextcloud.Requests.Single().RequestMessage!;

    // ---------------------------------------------------------------- FindAsync

    [Fact]
    public async Task Find_reads_the_user_record_with_the_console_credential()
    {
        _nextcloud.Answer("GET", UserPath, 200, 100, Alice);

        var account = (await Accounts.FindAsync("alice", Ct)).Value;

        account.ShouldBe(new GroupwareAccount("alice", "alice@example.test", "Alice Liddell", Enabled: true, ManagedByIdentityProvider: true));
        var request = Single;
        request.Method.ShouldBe("GET");
        request.AbsolutePath.ShouldBe(UserPath);
        request.Query!["format"].ShouldBe(["json"]);
        request.Headers!["Authorization"].ShouldBe([NextcloudAdminServer.BasicHeader]);
        request.Headers["OCS-APIRequest"].ShouldBe(["true"]);
        request.Headers["Accept"].Single().ShouldContain("application/json");
        request.Headers.ContainsKey("Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task Find_reports_a_local_disabled_account_without_an_address()
    {
        var data = JsonNode.Parse(Alice)!.AsObject();
        data["backend"] = "Database";
        data["enabled"] = false;
        data["email"] = null;
        data.Remove("displayname");
        _nextcloud.Answer("GET", UserPath, 200, 100, data.ToJsonString());

        var account = (await Accounts.FindAsync("alice", Ct)).Value;

        account.ShouldBe(new GroupwareAccount("alice", null, "Alice Liddell", Enabled: false, ManagedByIdentityProvider: false));
    }

    [Fact]
    public async Task Find_returns_the_uid_nextcloud_stores()
    {
        var data = JsonNode.Parse(Alice)!.AsObject();
        data["id"] = "Alice";
        data["email"] = "";
        _nextcloud.Answer("GET", UserPath, 200, 100, data.ToJsonString());

        var account = (await Accounts.FindAsync("alice", Ct)).Value;

        account.Uid.ShouldBe("Alice");
        account.Email.ShouldBeNull();
    }

    [Fact]
    public async Task Configured_backends_replace_the_user_oidc_default()
    {
        using var nextcloud = new NextcloudAdminServer(settings: new() { ["NextcloudProvisioning:IdentityProviderBackends:0"] = "LDAP" });
        nextcloud.Answer("GET", UserPath, 200, 100, Alice);

        (await nextcloud.Get<IGroupwareAccounts>().FindAsync("alice", Ct)).Value.ManagedByIdentityProvider.ShouldBeFalse();
    }

    [Theory]
    [InlineData(404)] // OCSNotFoundException("User does not exist"), carried in an HTTP 200 by v1
    [InlineData(998)] // the user exists but the caller may not see it, or the route is unknown
    public async Task Find_maps_ocs_not_found_to_not_found(int ocsStatus)
    {
        _nextcloud.Answer("GET", UserPath, 200, ocsStatus, message: "User does not exist");

        (await Accounts.FindAsync("alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Find_escapes_the_uid()
    {
        _nextcloud.Server.Given(WireMock.RequestBuilders.Request.Create().UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithBody(NextcloudAdminServer.Envelope(998)));

        await Accounts.FindAsync("o'brien test@x?y#z", Ct);

        // WireMock reports the path decoded: '?' and '#' stayed in the uid instead of starting a query or fragment.
        Single.Path.ShouldBe("/ocs/v1.php/cloud/users/o'brien test@x?y#z");
        Single.Query!["format"].ShouldBe(["json"]);
        Single.Url.ShouldContain("%3Fy%23z?format=json");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("..")]
    [InlineData("a/b")]
    public async Task Unusable_uids_are_refused_without_a_request(string uid)
    {
        (await Accounts.FindAsync(uid, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await Accounts.SetEnabledAsync(uid, false, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await Accounts.DeleteAsync(uid, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await Accounts.PreProvisionAsync(uid, "a@example.test", "A", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _nextcloud.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unreadable_user_record_is_unavailable()
    {
        _nextcloud.Answer("GET", UserPath, 200, 100, "[]");

        (await Accounts.FindAsync("alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    // ---------------------------------------------------------------- PreProvisionAsync

    [Fact]
    public async Task Pre_provisioning_posts_to_user_oidc_with_the_numeric_provider_id()
    {
        _nextcloud.Answer("POST", PreProvisionPath, 200, 200, """{"user_id":"alice"}""");

        (await Accounts.PreProvisionAsync("alice", "alice@example.test", "Alice Liddell", Ct)).IsSuccess.ShouldBeTrue();

        var request = Single;
        request.Method.ShouldBe("POST");
        request.AbsolutePath.ShouldBe(PreProvisionPath);
        request.Query!["format"].ShouldBe(["json"]);
        request.Headers!["Authorization"].ShouldBe([NextcloudAdminServer.BasicHeader]);
        request.Headers["OCS-APIRequest"].ShouldBe(["true"]);
        request.Headers["Content-Type"].Single().ShouldStartWith("application/json");
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        body.ToJsonString().ShouldBe("""{"providerId":2,"userId":"alice","displayName":"Alice Liddell","email":"alice@example.test"}""");
    }

    [Fact]
    public async Task Pre_provisioning_omits_an_empty_display_name()
    {
        _nextcloud.Answer("POST", PreProvisionPath, 200, 200, """{"user_id":"alice"}""");

        (await Accounts.PreProvisionAsync("alice", "alice@example.test", " ", Ct)).IsSuccess.ShouldBeTrue();

        JsonNode.Parse(Single.Body!)!.AsObject().ContainsKey("displayName").ShouldBeFalse();
    }

    [Fact]
    public async Task Pre_provisioning_an_existing_user_is_success()
    {
        // user_oidc's createUser is getOrCreate: an existing user is answered 200 with its uid, like a new one.
        _nextcloud.Answer("POST", PreProvisionPath, 200, 200, """{"user_id":"alice"}""");

        (await Accounts.PreProvisionAsync("alice", "alice@example.test", "Alice", Ct)).IsSuccess.ShouldBeTrue();
        (await Accounts.PreProvisionAsync("alice", "alice@example.test", "Alice", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Pre_provisioning_under_another_uid_is_a_conflict()
    {
        _nextcloud.Answer("POST", PreProvisionPath, 200, 200, """{"user_id":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"}""");

        var error = (await Accounts.PreProvisionAsync("alice", "alice@example.test", "Alice", Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Conflict);
        error.Message.ShouldContain("--unique-uid=0");
    }

    [Fact]
    public async Task Pre_provisioning_without_the_user_oidc_route_is_unsupported()
    {
        // ocs/v2.php's ResourceNotFoundException path: OCS 998 rendered by V2Response as HTTP 404.
        _nextcloud.Answer("POST", PreProvisionPath, 404, 998, message: "Invalid query, please check the syntax. API specifications are here: http://www.freedesktop.org/wiki/Specifications/open-collaboration-services.\n");

        (await Accounts.PreProvisionAsync("alice", "alice@example.test", "Alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
    }

    [Fact]
    public async Task A_non_ocs_404_is_not_mistaken_for_a_missing_app()
    {
        _nextcloud.AnswerRaw("POST", PreProvisionPath, 404, "<html><body>Not Found</body></html>", "text/html");

        (await Accounts.PreProvisionAsync("alice", "alice@example.test", "Alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Pre_provisioning_conflict_is_a_conflict()
    {
        _nextcloud.Answer("POST", PreProvisionPath, 409, 409, message: "User exists");

        (await Accounts.PreProvisionAsync("alice", "alice@example.test", "Alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public async Task Pre_provisioning_is_never_retried()
    {
        _nextcloud.AnswerRaw("POST", PreProvisionPath, 503, "");

        (await Accounts.PreProvisionAsync("alice", "alice@example.test", "Alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _nextcloud.Requests.Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_provider_identifier_is_resolved_through_the_provider_list()
    {
        using var nextcloud = new NextcloudAdminServer(settings: new() { ["NextcloudProvisioning:OidcProviderId"] = "keycloak" });
        nextcloud.Answer("GET", ProviderPath, 200, 200, """
            [{"id":1,"identifier":"other","clientId":"x","settings":{}},{"id":7,"identifier":"keycloak","clientId":"nextcloud","settings":{"uniqueUid":false}}]
            """);
        nextcloud.Answer("POST", PreProvisionPath, 200, 200, """{"user_id":"alice"}""");

        (await nextcloud.Get<IGroupwareAccounts>().PreProvisionAsync("alice", "alice@example.test", "Alice", Ct)).IsSuccess.ShouldBeTrue();

        var post = nextcloud.Requests.Single(e => e.RequestMessage!.Method == "POST").RequestMessage!;
        JsonNode.Parse(post.Body!)!["providerId"]!.GetValue<int>().ShouldBe(7);
        var get = nextcloud.Requests.Single(e => e.RequestMessage!.Method == "GET").RequestMessage!;
        get.Headers!["OCS-APIRequest"].ShouldBe(["true"]);
        get.Headers["Authorization"].ShouldBe([NextcloudAdminServer.BasicHeader]);
    }

    [Fact]
    public async Task An_unknown_provider_identifier_is_unavailable_and_nothing_is_created()
    {
        using var nextcloud = new NextcloudAdminServer(settings: new() { ["NextcloudProvisioning:OidcProviderId"] = "keycloak" });
        nextcloud.Answer("GET", ProviderPath, 200, 200, """[{"id":1,"identifier":"other"}]""");

        var error = (await nextcloud.Get<IGroupwareAccounts>().PreProvisionAsync("alice", "alice@example.test", "Alice", Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        error.Message.ShouldContain("OidcProviderId");
        nextcloud.Requests.ShouldAllBe(e => e.RequestMessage!.Method == "GET");
    }

    [Fact]
    public async Task Without_the_provider_list_route_pre_provisioning_is_unsupported()
    {
        using var nextcloud = new NextcloudAdminServer(settings: new() { ["NextcloudProvisioning:OidcProviderId"] = "keycloak" });
        nextcloud.Answer("GET", ProviderPath, 404, 998);

        (await nextcloud.Get<IGroupwareAccounts>().PreProvisionAsync("alice", "alice@example.test", "Alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
    }

    // ---------------------------------------------------------------- SetEnabledAsync

    [Theory]
    [InlineData(true, "enable")]
    [InlineData(false, "disable")]
    public async Task Set_enabled_puts_enable_or_disable(bool enabled, string verb)
    {
        _nextcloud.Answer("PUT", $"{UserPath}/{verb}", 200, 100);

        (await Accounts.SetEnabledAsync("alice", enabled, Ct)).IsSuccess.ShouldBeTrue();

        var request = Single;
        request.Method.ShouldBe("PUT");
        request.AbsolutePath.ShouldBe($"{UserPath}/{verb}");
        request.Headers!["Authorization"].ShouldBe([NextcloudAdminServer.BasicHeader]);
        request.Headers["OCS-APIRequest"].ShouldBe(["true"]);
    }

    [Fact]
    public async Task Set_enabled_on_a_missing_user_is_not_found()
    {
        // setEnabled throws OCSException('', 101) when the target does not exist (or is the caller).
        _nextcloud.Answer("PUT", $"{UserPath}/disable", 200, 101);

        (await Accounts.SetEnabledAsync("alice", false, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Set_enabled_without_password_confirmation_is_unavailable()
    {
        // #[PasswordConfirmationRequired]: NotConfirmedException is a 403, which v1 carries in an HTTP 200.
        _nextcloud.Answer("PUT", $"{UserPath}/disable", 200, 403, message: "Password confirmation is required");

        var error = (await Accounts.SetEnabledAsync("alice", false, Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        error.Message.ShouldContain("administrator");
    }

    [Fact]
    public async Task Writes_are_never_retried()
    {
        _nextcloud.AnswerRaw("PUT", $"{UserPath}/disable", 502, "Bad Gateway", "text/plain");

        (await Accounts.SetEnabledAsync("alice", false, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _nextcloud.Requests.Count().ShouldBe(1);
    }

    // ---------------------------------------------------------------- DeleteAsync

    [Fact]
    public async Task Delete_deletes_the_user()
    {
        _nextcloud.Answer("DELETE", UserPath, 200, 100);

        (await Accounts.DeleteAsync("alice", Ct)).IsSuccess.ShouldBeTrue();

        var request = Single;
        request.Method.ShouldBe("DELETE");
        request.AbsolutePath.ShouldBe(UserPath);
        request.Headers!["Authorization"].ShouldBe([NextcloudAdminServer.BasicHeader]);
        request.Headers["OCS-APIRequest"].ShouldBe(["true"]);
    }

    [Fact]
    public async Task Delete_of_a_missing_user_is_not_found()
    {
        _nextcloud.Answer("DELETE", UserPath, 200, 998);

        (await Accounts.DeleteAsync("alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_refused_delete_is_unavailable_not_not_found()
    {
        _nextcloud.Answer("DELETE", UserPath, 200, 101);

        (await Accounts.DeleteAsync("alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    // ---------------------------------------------------------------- shared failures

    [Fact]
    public async Task A_rejected_console_credential_is_unavailable_not_auth_failed()
    {
        // V1Response: OCS 997 → HTTP 401 with the envelope.
        _nextcloud.Answer("GET", UserPath, 401, 997, message: "Unauthorised");

        var error = (await Accounts.FindAsync("alice", Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        error.Message.ShouldContain("NextcloudProvisioning:Password");
        error.Message.ShouldNotContain(NextcloudAdminServer.Password);
    }

    [Fact]
    public async Task A_server_error_is_unavailable()
    {
        _nextcloud.Answer("DELETE", UserPath, 500, 996, message: "Internal Server Error");

        (await Accounts.DeleteAsync("alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task A_non_ocs_answer_to_find_is_unavailable_not_missing()
    {
        // A wrong BaseUrl must not make every user look Missing (and be offered for creation).
        _nextcloud.AnswerRaw("GET", UserPath, 404, "<html>nope</html>", "text/html");

        (await Accounts.FindAsync("alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task An_unreachable_server_is_unavailable()
    {
        var accounts = Accounts;
        _nextcloud.Server.Stop();

        (await accounts.FindAsync("alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }
}
