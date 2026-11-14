using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Jmap.Core.Auth;
using Nightware.Adapters.Jmap.Core.Http;

namespace Nightware.Adapters.Jmap.Core.Health;

/// <summary>
/// Probes the JMAP session endpoint without credentials. Stalwart answers an anonymous
/// session request with 200 (no accounts); 401/403 also prove it is up. 5xx, timeouts and
/// connection failures are unhealthy.
/// </summary>
internal sealed class JmapSessionHealthCheck(IHttpClientFactory httpClientFactory, IOptions<JmapOptions> options) : IHealthCheck
{
    public const string Name = "jmap";

    public static readonly string[] Tags = ["backend"];

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var url = options.Value.SessionUrl;
        if (url is null)
        {
            return HealthCheckResult.Unhealthy("Jmap:SessionUrl is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url).AsAnonymous();
        var sent = await JmapHttp.SendAsync(httpClientFactory.CreateClient(JmapHttpClients.Health), request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return HealthCheckResult.Unhealthy(sent.Error.Message);
        }

        using var response = sent.Value;
        var status = (int)response.StatusCode;
        return status < 500
            ? HealthCheckResult.Healthy($"JMAP session endpoint answered {status}.")
            : HealthCheckResult.Unhealthy($"JMAP session endpoint answered {status}.");
    }
}
