using System.Net;

using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;

namespace Nightware.Adapters.Directory.Tests.Administration;

public sealed class KeycloakBackendStatusTests : IDisposable
{
    private readonly KeycloakStub _keycloak = new();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdapterHost Host() =>
        AdapterHost.CreateConsole(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = "0" });

    private static IBackendStatus Keycloak(AdapterHost host) => host.Scoped<IEnumerable<IBackendStatus>>().Single();

    [Fact]
    public async Task Healthy_when_the_token_is_issued_and_the_admin_api_answers()
    {
        _keycloak.GivenServiceAccountToken();
        _keycloak.GivenAdmin("/users/count", 200, "12");
        await using AdapterHost host = Host();

        BackendHealth health = await Keycloak(host).CheckAsync(Ct);

        health.ShouldBe(new BackendHealth(BackendKind.Identity, true));
        _keycloak.Admin("GET", "/users/count").Single().Headers!["Authorization"].Single().ShouldBe("Bearer sa-token-1");
    }

    [Fact]
    public async Task Unhealthy_when_no_token_can_be_obtained()
    {
        _keycloak.GivenServiceAccountTokenFails(HttpStatusCode.Unauthorized, Fixture.Load("error-unauthorized-client.json"));
        await using AdapterHost host = Host();

        BackendHealth health = await Keycloak(host).CheckAsync(Ct);

        health.Healthy.ShouldBeFalse();
        health.Backend.ShouldBe(BackendKind.Identity);
        string detail = health.Detail.ShouldNotBeNull();
        detail.ShouldContain("service-account token");
        detail.ShouldNotContain("nightware-admin-dev-secret");
        _keycloak.AdminRequests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(403, "not permitted")]
    [InlineData(503, "unavailable")]
    public async Task Unhealthy_when_the_admin_api_refuses(int status, string detail)
    {
        _keycloak.GivenServiceAccountToken();
        _keycloak.GivenAdmin("/users/count", status);
        await using AdapterHost host = Host();

        BackendHealth health = await Keycloak(host).CheckAsync(Ct);

        health.Healthy.ShouldBeFalse();
        health.Detail!.ShouldContain(detail);
    }
}
