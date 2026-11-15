using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Net.Http.Headers;

namespace Nightware.Api.Hosting;

/// <summary>
/// The readiness report (every backend health check), shared by all callers for <see cref="Lifetime"/>.
/// </summary>
/// <remarks>
/// <c>/healthz</c> is anonymous, exempt from the rate limiter and public through Caddy, and one run
/// calls Stalwart, Nextcloud and Keycloak. Without a cache a loop on it turns every hit into three
/// backend requests. Callers within the lifetime get the last report, Unhealthy included; a refresh is
/// single-flight (concurrent callers await the same run) and never runs on a caller's abort token, so
/// one dropped connection cannot cancel the run the others wait for. A run that throws is not kept.
/// Liveness (<c>/healthz/live</c>) runs no checks and is not cached.
/// </remarks>
internal sealed class ReadinessCache(HealthCheckService health, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private Task<HealthReport>? _current;
    private DateTimeOffset _expires;

    public Task<HealthReport> GetAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_current is null || _current.IsFaulted || _current.IsCanceled
                || (_current.IsCompleted && clock.GetUtcNow() >= _expires))
            {
                _current = RunAsync();
            }

            return _current.WaitAsync(cancellationToken);
        }
    }

    private async Task<HealthReport> RunAsync()
    {
        await Task.Yield(); // never run the checks while holding the lock
        var report = await health.CheckHealthAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_gate)
        {
            _expires = clock.GetUtcNow() + Lifetime;
        }

        return report;
    }
}

internal static class ReadinessEndpoint
{
    /// <summary>
    /// Maps readiness on <paramref name="path"/> for every method, answering as <c>MapHealthChecks</c>
    /// does: the status as plain text, 200 for Healthy/Degraded, 503 for Unhealthy, never cacheable.
    /// </summary>
    public static IEndpointConventionBuilder MapReadiness(this IEndpointRouteBuilder endpoints, string path) =>
        endpoints.Map(path, static async http =>
        {
            var report = await http.RequestServices.GetRequiredService<ReadinessCache>()
                .GetAsync(http.RequestAborted).ConfigureAwait(false);
            http.Response.StatusCode = report.Status == HealthStatus.Unhealthy
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK;
            http.Response.ContentType = "text/plain";
            http.Response.Headers[HeaderNames.CacheControl] = "no-store, no-cache";
            http.Response.Headers[HeaderNames.Pragma] = "no-cache";
            http.Response.Headers[HeaderNames.Expires] = "Thu, 01 Jan 1970 00:00:00 GMT";
            await http.Response.WriteAsync(report.Status.ToString(), http.RequestAborted).ConfigureAwait(false);
        });
}
