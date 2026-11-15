using System.Net;

using Microsoft.AspNetCore.Builder;

using Nightware.Api.Graph;
using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Api.Tests.Hosting;

/// <summary>
/// The whole failure chain through a real adapter: a backend that is slow, down or overloaded
/// reaches NightMail as a Graph 503 with an integer <c>Retry-After</c>, which its
/// <c>RetryInterceptor</c> honours. The backend is Keycloak's token endpoint (the RFC 8693 exchange
/// every mail and groupware call starts with), driven through <see cref="IBackendCredentials"/>.
/// </summary>
public sealed class BackendFailureChainTests : IDisposable
{
    private const string TokenPath = "/realms/nightware/protocol/openid-connect/token";

    private readonly WireMockServer _keycloak = WireMockServer.Start();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _keycloak.Dispose();

    private Task<TestHost> StartAsync(string requestTimeout = "00:00:05", string budget = "00:00:10") =>
        TestHost.StartAsync(
            static s => s.AddGraphProbes(static g => g.MapGet("/probe/mail-credential", static async (IBackendCredentials credentials, CancellationToken ct) =>
                GraphResults.Match(await credentials.ForAsync(Backend.Mail, ct), static _ => Microsoft.AspNetCore.Http.Results.Ok()))),
            configuration: new Dictionary<string, string?>
            {
                ["Directory:KeycloakBaseUrl"] = _keycloak.Url,
                ["Directory:RequestTimeout"] = requestTimeout,
                ["Directory:MaxRetryAttempts"] = "0",
                ["BackendCredentials:Mail:Strategy"] = "TokenExchange",
                ["BackendCredentials:Mail:Audience"] = "stalwart",
                ["RequestTimeouts:Default"] = budget,
            });

    [Fact]
    public async Task An_adapter_timeout_is_a_503_with_integer_retry_after()
    {
        _keycloak.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));
        await using var host = await StartAsync(requestTimeout: "00:00:00.200");

        var response = await host.AuthorizedClient().GetAsync("/v1.0/probe/mail-credential", Ct);

        await ShouldBeRetryable503(response, expectedRetryAfter: "5");
    }

    [Fact]
    public async Task An_unreachable_backend_is_a_503_with_integer_retry_after()
    {
        _keycloak.Stop();
        await using var host = await StartAsync();

        var response = await host.AuthorizedClient().GetAsync("/v1.0/probe/mail-credential", Ct);

        await ShouldBeRetryable503(response, expectedRetryAfter: "5");
    }

    [Fact]
    public async Task A_backends_own_retry_after_is_passed_through_as_seconds()
    {
        _keycloak.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503).WithHeader("Retry-After", "17"));
        await using var host = await StartAsync();

        var response = await host.AuthorizedClient().GetAsync("/v1.0/probe/mail-credential", Ct);

        await ShouldBeRetryable503(response, expectedRetryAfter: "17");
    }

    [Fact]
    public async Task A_backend_http_date_retry_after_still_reaches_the_client_as_integer_seconds()
    {
        _keycloak.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503).WithHeader("Retry-After", DateTimeOffset.UtcNow.AddSeconds(120).ToString("R")));
        await using var host = await StartAsync();

        var response = await host.AuthorizedClient().GetAsync("/v1.0/probe/mail-credential", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        int.Parse(response.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture).ShouldBeInRange(60, 121);
    }

    [Fact]
    public async Task The_facade_budget_answers_before_a_slower_adapter_timeout()
    {
        _keycloak.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));
        await using var host = await StartAsync(requestTimeout: "00:00:30", budget: "00:00:00.300");

        var response = await host.AuthorizedClient().GetAsync("/v1.0/probe/mail-credential", Ct);

        await ShouldBeRetryable503(response, expectedRetryAfter: "5");
        (await response.ReadObjectAsync()).Str("error.message")!.ShouldContain("did not complete within");
    }

    [Fact]
    public async Task A_rejected_subject_token_is_a_401_so_the_client_re_authenticates()
    {
        _keycloak.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBodyAsJson(new { error = "invalid_grant", error_description = "Invalid token" }));
        await using var host = await StartAsync();

        var response = await host.AuthorizedClient().GetAsync("/v1.0/probe/mail-credential", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("InvalidAuthenticationToken");
    }

    [Fact]
    public async Task Facade_misconfiguration_is_a_503_not_a_401()
    {
        // ADR 0001 §34: a bad client secret must not make every user re-authenticate.
        _keycloak.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401).WithBodyAsJson(new { error = "unauthorized_client", error_description = "Invalid client credentials" }));
        await using var host = await StartAsync();

        var response = await host.AuthorizedClient().GetAsync("/v1.0/probe/mail-credential", Ct);

        await ShouldBeRetryable503(response, expectedRetryAfter: "5");
    }

    [Fact]
    public async Task Recovery_needs_no_restart()
    {
        _keycloak.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503));
        await using var host = await StartAsync();
        var client = host.AuthorizedClient();
        (await client.GetAsync("/v1.0/probe/mail-credential", Ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        _keycloak.Reset();
        _keycloak.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { access_token = "exchanged", token_type = "Bearer", expires_in = 300 }));

        (await client.GetAsync("/v1.0/probe/mail-credential", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task ShouldBeRetryable503(HttpResponseMessage response, string expectedRetryAfter)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.GetValues("Retry-After").Single().ShouldBe(expectedRetryAfter);
        var body = await response.ReadObjectAsync();
        body.Str("error.code").ShouldBe("serviceNotAvailable");
        body.Str("error.innerError.request-id").ShouldNotBeNullOrEmpty();
    }
}
