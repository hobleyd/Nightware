using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Nightware.Adapters.Dav.Core;

/// <summary>Health check "dav" (tag <c>backend</c>) over <see cref="DavHealthProbe"/>.</summary>
internal sealed class DavHealthCheck(DavHealthProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = await probe.CheckAsync(cancellationToken).ConfigureAwait(false);
        return status.IsHealthy ? HealthCheckResult.Healthy(status.Description) : HealthCheckResult.Unhealthy(status.Description);
    }
}
