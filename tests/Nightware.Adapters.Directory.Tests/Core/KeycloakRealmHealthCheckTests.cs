using Microsoft.Extensions.Diagnostics.HealthChecks;

using Nightware.Adapters.Directory.Tests.TestSupport;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Directory.Tests.Core;

public sealed class KeycloakRealmHealthCheckTests : IDisposable
{
    private readonly KeycloakStub _keycloak = new();

    public void Dispose() => _keycloak.Dispose();

    private async Task<HealthReport> CheckAsync(string? url = null)
    {
        await using AdapterHost host = AdapterHost.Create(url ?? _keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = "0" });
        return await host.Get<HealthCheckService>().CheckHealthAsync(r => r.Tags.Contains("backend"), TestContext.Current.CancellationToken);
    }

    private void GivenDiscovery(int status, string body) =>
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.DiscoveryPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    [Fact]
    public async Task Healthy_when_the_realm_publishes_an_issuer()
    {
        GivenDiscovery(200, Fixture.Load("discovery.json"));

        HealthReport report = await CheckAsync();

        report.Entries.ShouldContainKey("keycloak");
        report.Entries["keycloak"].Status.ShouldBe(HealthStatus.Healthy);
        report.Entries["keycloak"].Tags.ShouldContain("backend");
        report.Entries["keycloak"].Description!.ShouldContain("https://auth.nightware.localhost/realms/nightware");
    }

    [Fact]
    public async Task Unhealthy_when_the_realm_is_missing() // Keycloak answers 404 for an unknown realm
    {
        GivenDiscovery(404, """{"error":"Realm does not exist"}""");

        (await CheckAsync()).Entries["keycloak"].Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Theory]
    [InlineData("""{"token_endpoint":"x"}""")]
    [InlineData("not json")]
    public async Task Unhealthy_without_an_issuer(string body)
    {
        GivenDiscovery(200, body);

        (await CheckAsync()).Entries["keycloak"].Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Unhealthy_when_keycloak_is_unreachable()
    {
        string url = _keycloak.Url;
        _keycloak.Dispose();

        (await CheckAsync(url)).Entries["keycloak"].Status.ShouldBe(HealthStatus.Unhealthy);
    }
}
