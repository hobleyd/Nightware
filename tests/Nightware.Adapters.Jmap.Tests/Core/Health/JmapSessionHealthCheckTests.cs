using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nightware.Adapters.Jmap.Tests.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Jmap.Tests.Core.Health;

public sealed class JmapSessionHealthCheckTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static async Task<HealthReport> CheckAsync(JmapTestHost host) =>
        await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Tags.Contains("backend"), Ct);

    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(307)]
    public async Task Answering_session_endpoint_is_healthy(int status)
    {
        await using var host = new JmapTestHost();
        host.Server.Given(Request.Create().WithPath("/jmap/session").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(Fixtures.AnonymousSession));

        var report = await CheckAsync(host);

        report.Entries.ShouldContainKey("jmap");
        report.Status.ShouldBe(HealthStatus.Healthy);
        JmapTestHost.Authorization(host.Server.LogEntries.ShouldHaveSingleItem()).ShouldBeNull();
    }

    [Fact]
    public async Task Server_errors_are_unhealthy()
    {
        await using var host = new JmapTestHost();
        host.Server.Given(Request.Create().WithPath("/jmap/session").UsingGet()).RespondWith(Response.Create().WithStatusCode(503));

        (await CheckAsync(host)).Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Unreachable_servers_are_unhealthy()
    {
        await using var host = new JmapTestHost(s => s["Jmap:SessionUrl"] = "http://127.0.0.1:1/jmap/session");

        (await CheckAsync(host)).Status.ShouldBe(HealthStatus.Unhealthy);
    }
}
