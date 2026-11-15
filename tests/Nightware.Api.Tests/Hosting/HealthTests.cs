using System.Net;

using Nightware.Api.Composition;
using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Hosting;

/// <summary>Liveness never depends on a backend; readiness does.</summary>
public sealed class HealthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Liveness_is_healthy_while_every_backend_is_unreachable()
    {
        // TestHost's backends (auth.test.example, the default Jmap/Dav URLs) do not resolve.
        await using var host = await TestHost.StartAsync();

        var live = await host.Client.GetAsync(NightwareComposition.LivenessPath, Ct);
        var ready = await host.Client.GetAsync(NightwareComposition.HealthPath, Ct);

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await live.Content.ReadAsStringAsync(Ct)).ShouldBe("Healthy");
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}
