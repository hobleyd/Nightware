using System.Net;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using Nightware.Adapters.Directory.Core;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.Core;

public sealed class KeycloakTokenClientTests : IDisposable
{
    private readonly KeycloakStub _keycloak = new();

    public void Dispose() => _keycloak.Dispose();

    private AdapterHost Host(TimeProvider? time = null, int retries = 2) =>
        AdapterHost.Create(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = retries.ToString(System.Globalization.CultureInfo.InvariantCulture) }, time);

    [Fact]
    public async Task Client_credentials_request_authenticates_with_client_secret_basic()
    {
        _keycloak.GivenServiceAccountToken();
        await using AdapterHost host = Host();

        Result<AccessToken> token = await host.Get<IKeycloakTokenClient>().GetServiceAccountTokenAsync(TestContext.Current.CancellationToken);

        token.IsSuccess.ShouldBeTrue();
        token.Value.Value.ShouldBe("sa-token-1");
        WireMock.RequestMessage request = (WireMock.RequestMessage)_keycloak.TokenRequests.Single().RequestMessage!;
        request.Method.ShouldBe("POST");
        request.Body.ShouldBe("grant_type=client_credentials&scope=kc-admin-api");
        string expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{KeycloakStub.ClientId}:{KeycloakStub.ClientSecret}"));
        request.Headers!["Authorization"].ShouldHaveSingleItem().ShouldBe($"Basic {expected}");
    }

    [Fact]
    public async Task Service_account_token_is_cached_until_shortly_before_expiry()
    {
        FakeTimeProvider time = new(DateTimeOffset.Parse("2026-11-15T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        _keycloak.GivenServiceAccountToken(expiresIn: 900);
        await using AdapterHost host = Host(time, retries: 0);
        IKeycloakTokenClient client = host.Get<IKeycloakTokenClient>();
        CancellationToken ct = TestContext.Current.CancellationToken;

        (await client.GetServiceAccountTokenAsync(ct)).Value.Value.ShouldBe("sa-token-1");
        time.Advance(TimeSpan.FromSeconds(869)); // 31 s left: still outside the 30 s skew
        (await client.GetServiceAccountTokenAsync(ct)).Value.Value.ShouldBe("sa-token-1");
        _keycloak.ClientCredentialsHits.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(1)); // inside the skew → refresh
        (await client.GetServiceAccountTokenAsync(ct)).Value.Value.ShouldBe("sa-token-2");
        _keycloak.ClientCredentialsHits.ShouldBe(2);
    }

    [Fact]
    public async Task Very_short_lived_token_is_refreshed_at_half_its_lifetime()
    {
        FakeTimeProvider time = new();
        _keycloak.GivenServiceAccountToken(expiresIn: 40); // shorter than 2 × 30 s skew
        await using AdapterHost host = Host(time, retries: 0);
        IKeycloakTokenClient client = host.Get<IKeycloakTokenClient>();
        CancellationToken ct = TestContext.Current.CancellationToken;

        await client.GetServiceAccountTokenAsync(ct);
        time.Advance(TimeSpan.FromSeconds(19));
        (await client.GetServiceAccountTokenAsync(ct)).Value.Value.ShouldBe("sa-token-1");
        time.Advance(TimeSpan.FromSeconds(1));
        (await client.GetServiceAccountTokenAsync(ct)).Value.Value.ShouldBe("sa-token-2");
    }

    [Fact]
    public async Task Concurrent_callers_share_a_single_in_flight_refresh()
    {
        _keycloak.GivenServiceAccountToken(delay: TimeSpan.FromMilliseconds(300));
        await using AdapterHost host = Host();
        IKeycloakTokenClient client = host.Get<IKeycloakTokenClient>();

        Result<AccessToken>[] results = await Task.WhenAll(
            Enumerable.Range(0, 25).Select(_ => Task.Run(() => client.GetServiceAccountTokenAsync(TestContext.Current.CancellationToken))));

        results.ShouldAllBe(r => r.IsSuccess && r.Value.Value == "sa-token-1");
        _keycloak.ClientCredentialsHits.ShouldBe(1);
    }

    [Fact]
    public async Task Invalidating_the_current_token_forces_a_refresh()
    {
        _keycloak.GivenServiceAccountToken();
        await using AdapterHost host = Host();
        IKeycloakTokenClient client = host.Get<IKeycloakTokenClient>();
        CancellationToken ct = TestContext.Current.CancellationToken;

        AccessToken first = (await client.GetServiceAccountTokenAsync(ct)).Value;
        client.InvalidateServiceAccountToken(first);
        AccessToken second = (await client.GetServiceAccountTokenAsync(ct)).Value;

        second.Value.ShouldBe("sa-token-2");
        client.InvalidateServiceAccountToken(first); // stale reference: no effect
        (await client.GetServiceAccountTokenAsync(ct)).Value.ShouldBeSameAs(second);
        _keycloak.ClientCredentialsHits.ShouldBe(2);
    }

    [Fact]
    public async Task Client_credentials_is_retried_on_a_transient_failure()
    {
        _keycloak.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(KeycloakStub.TokenPath).UsingPost())
            .InScenario("flaky").WillSetStateTo("recovered")
            .AtPriority(1)
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(503));
        _keycloak.GivenServiceAccountToken();
        await using AdapterHost host = Host();

        Result<AccessToken> token = await host.Get<IKeycloakTokenClient>().GetServiceAccountTokenAsync(TestContext.Current.CancellationToken);

        token.IsSuccess.ShouldBeTrue();
        _keycloak.ClientCredentialsHits.ShouldBe(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "error-unauthorized-client.json")]
    [InlineData(HttpStatusCode.BadRequest, "error-audience-not-found.json")]
    public async Task Client_credentials_rejection_is_backend_unavailable_not_auth_failed(HttpStatusCode status, string fixture)
    {
        _keycloak.GivenServiceAccountTokenFails(status, Fixture.Load(fixture));
        await using AdapterHost host = Host();

        Result<AccessToken> token = await host.Get<IKeycloakTokenClient>().GetServiceAccountTokenAsync(TestContext.Current.CancellationToken);

        token.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _keycloak.ClientCredentialsHits.ShouldBe(1); // 4xx is not transient
        host.Logs.Lines.ShouldContain(l => l.StartsWith("Error", StringComparison.Ordinal) && l.Contains("client_credentials", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Throttled_client_credentials_is_not_retried()
    {
        _keycloak.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(KeycloakStub.TokenPath).UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(429).WithHeader("Retry-After", "30"));
        await using AdapterHost host = Host(retries: 2);

        Result<AccessToken> token = await host.Get<IKeycloakTokenClient>().GetServiceAccountTokenAsync(TestContext.Current.CancellationToken);

        token.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        token.Error.RetryAfterSeconds.ShouldBe(30);
        _keycloak.ClientCredentialsHits.ShouldBe(1);
    }

    [Fact]
    public async Task Failed_client_credentials_is_not_cached()
    {
        _keycloak.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(KeycloakStub.TokenPath).UsingPost())
            .InScenario("down").WillSetStateTo("up")
            .AtPriority(1)
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(401).WithBody(Fixture.Load("error-unauthorized-client.json")));
        _keycloak.GivenServiceAccountToken();
        await using AdapterHost host = Host();
        IKeycloakTokenClient client = host.Get<IKeycloakTokenClient>();

        (await client.GetServiceAccountTokenAsync(TestContext.Current.CancellationToken)).IsSuccess.ShouldBeFalse();
        (await client.GetServiceAccountTokenAsync(TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Exchange_sends_rfc8693_standard_token_exchange_parameters()
    {
        _keycloak.GivenExchange("stalwart", "exchanged-stalwart");
        await using AdapterHost host = Host();
        string subject = Jwt.WithExpiry(DateTimeOffset.UtcNow.AddMinutes(10));

        Result<AccessToken> token = await host.Get<IKeycloakTokenClient>().ExchangeAsync(subject, "stalwart", TestContext.Current.CancellationToken);

        token.Value.Value.ShouldBe("exchanged-stalwart");
        Dictionary<string, string> form = ParseForm(_keycloak.TokenRequests.Single().RequestMessage!.Body!);
        form.ShouldBe(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["requested_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["audience"] = "stalwart",
        }, ignoreOrder: true);
        _keycloak.TokenRequests.Single().RequestMessage!.Headers!["Authorization"].Single().ShouldStartWith("Basic ");
    }

    [Fact]
    public async Task Exchange_expiry_comes_from_expires_in()
    {
        FakeTimeProvider time = new(DateTimeOffset.Parse("2026-11-15T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        _keycloak.GivenExchange("nextcloud", "exchanged-nextcloud", expiresIn: 300);
        await using AdapterHost host = Host(time, retries: 0);

        Result<AccessToken> token = await host.Get<IKeycloakTokenClient>().ExchangeAsync("subject", "nextcloud", TestContext.Current.CancellationToken);

        token.Value.ExpiresAt.ShouldBe(time.GetUtcNow().AddSeconds(300));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "error-invalid-token.json", ErrorKind.BackendAuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, "error-access-denied.json", ErrorKind.BackendAuthFailed)]
    [InlineData(HttpStatusCode.BadRequest, "error-audience-not-available.json", ErrorKind.BackendUnavailable)]
    [InlineData(HttpStatusCode.BadRequest, "error-audience-not-found.json", ErrorKind.BackendUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized, "error-unauthorized-client.json", ErrorKind.BackendUnavailable)]
    public async Task Exchange_failures_map_to_the_right_error(HttpStatusCode status, string fixture, ErrorKind expected)
    {
        _keycloak.GivenExchangeFails(status, Fixture.Load(fixture));
        await using AdapterHost host = Host();

        Result<AccessToken> token = await host.Get<IKeycloakTokenClient>().ExchangeAsync("subject", "stalwart", TestContext.Current.CancellationToken);

        token.Error!.Kind.ShouldBe(expected);
        _keycloak.ExchangeHits.ShouldBe(1);
    }

    [Fact]
    public async Task Exchange_is_never_retried_even_on_a_transient_failure()
    {
        _keycloak.GivenExchangeFails(HttpStatusCode.ServiceUnavailable, null);
        await using AdapterHost host = Host(retries: 3);

        Result<AccessToken> token = await host.Get<IKeycloakTokenClient>().ExchangeAsync("subject", "stalwart", TestContext.Current.CancellationToken);

        token.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _keycloak.ExchangeHits.ShouldBe(1);
    }

    [Fact]
    public async Task Exchange_throttled_carries_retry_after()
    {
        _keycloak.GivenExchangeFails(HttpStatusCode.TooManyRequests, null, retryAfter: "7");
        await using AdapterHost host = Host();

        Result<AccessToken> token = await host.Get<IKeycloakTokenClient>().ExchangeAsync("subject", "stalwart", TestContext.Current.CancellationToken);

        token.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        token.Error.RetryAfterSeconds.ShouldBe(7);
    }

    [Fact]
    public async Task Unreachable_keycloak_is_backend_unavailable()
    {
        string url = _keycloak.Url;
        _keycloak.Dispose();
        await using AdapterHost host = AdapterHost.Create(url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = "0" });

        Result<AccessToken> exchanged = await host.Get<IKeycloakTokenClient>().ExchangeAsync("subject", "stalwart", TestContext.Current.CancellationToken);
        Result<AccessToken> serviceAccount = await host.Get<IKeycloakTokenClient>().GetServiceAccountTokenAsync(TestContext.Current.CancellationToken);

        exchanged.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        serviceAccount.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Timeout_is_backend_unavailable()
    {
        _keycloak.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(KeycloakStub.TokenPath).UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(3)));
        await using AdapterHost host = AdapterHost.Create(_keycloak.Url, new Dictionary<string, string?>
        {
            ["Directory:MaxRetryAttempts"] = "0",
            ["Directory:RequestTimeout"] = "00:00:00.200",
        });

        Result<AccessToken> token = await host.Get<IKeycloakTokenClient>().ExchangeAsync("subject", "stalwart", TestContext.Current.CancellationToken);

        token.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Theory]
    [InlineData("""{"access_token":"x","expires_in":900,"token_type":"N_A"}""")]
    [InlineData("""{"expires_in":900,"token_type":"Bearer"}""")]
    [InlineData("""{"access_token":"x","token_type":"Bearer"}""")]
    [InlineData("not json")]
    public async Task Malformed_success_response_is_backend_unavailable(string body)
    {
        _keycloak.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(KeycloakStub.TokenPath).UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body));
        await using AdapterHost host = Host();

        Result<AccessToken> token = await host.Get<IKeycloakTokenClient>().ExchangeAsync("subject", "stalwart", TestContext.Current.CancellationToken);

        token.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public void Access_token_ToString_is_redacted()
    {
        AccessToken token = new("secret-token-value", DateTimeOffset.UnixEpoch);

        token.ToString().ShouldNotContain("secret-token-value");
    }

    private void GivenTokenResponse(string json, int status = 200) =>
        _keycloak.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(KeycloakStub.TokenPath).UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(json));

    [Fact]
    public async Task A_refresh_token_is_obtained_by_exchange_and_renewed_by_a_refresh_grant()
    {
        GivenTokenResponse("""{"access_token":"a1","expires_in":900,"token_type":"Bearer","refresh_token":"r1","issued_token_type":"urn:ietf:params:oauth:token-type:refresh_token"}""");
        await using AdapterHost host = Host();
        var client = host.Get<IKeycloakTokenClient>();

        var issued = (await client.ExchangeForRefreshTokenAsync("subject", TestContext.Current.CancellationToken)).Value;
        var renewed = (await client.RefreshAsync("r1", TestContext.Current.CancellationToken)).Value;

        issued.RefreshToken.ShouldBe("r1");
        issued.ToString().ShouldNotContain("r1");
        renewed.Access.Value.ShouldBe("a1");
        var forms = _keycloak.TokenRequests.Select(r => ParseForm(r.RequestMessage!.Body!)).ToList();
        forms[0]["requested_token_type"].ShouldBe("urn:ietf:params:oauth:token-type:refresh_token");
        forms[0]["subject_token"].ShouldBe("subject");
        forms[0].ContainsKey("audience").ShouldBeFalse();
        forms[1].ShouldBe(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = "r1" }, ignoreOrder: true);
    }

    [Fact]
    public async Task No_refresh_token_in_the_answer_means_the_client_is_not_set_up_for_it()
    {
        GivenTokenResponse("""{"access_token":"a1","expires_in":900,"token_type":"Bearer"}""");
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakTokenClient>().ExchangeForRefreshTokenAsync("subject", TestContext.Current.CancellationToken)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task An_expired_session_is_the_subscribers_problem()
    {
        GivenTokenResponse("""{"error":"invalid_grant","error_description":"Session not active"}""", 400);
        await using AdapterHost host = Host();

        (await host.Get<IKeycloakTokenClient>().RefreshAsync("r1", TestContext.Current.CancellationToken)).Error!.Kind.ShouldBe(ErrorKind.BackendAuthFailed);
    }

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&').Select(static p => p.Split('=', 2)).ToDictionary(static p => Uri.UnescapeDataString(p[0]), static p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
}
