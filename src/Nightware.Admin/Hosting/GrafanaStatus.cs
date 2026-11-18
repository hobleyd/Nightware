using Microsoft.Extensions.Options;

using Nightware.Admin.Auth;

namespace Nightware.Admin.Hosting;

/// <summary>Grafana's answer to its health check; <see cref="Detail"/> says why it is not healthy.</summary>
public sealed record GrafanaHealth(bool Healthy, string? Detail = null);

/// <summary>Whether Grafana, the stack's telemetry, is up, for the dashboard's Systems panel.</summary>
public interface IGrafanaStatus
{
    Task<GrafanaHealth> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <c>GET /api/health</c>: anonymous, cheap, and 200 only while Grafana can reach its own database. Grafana is not a
/// backend the console administers, so this lives beside the dashboard rather than behind an Application port.
/// </summary>
internal sealed partial class GrafanaStatus(HttpClient http, IOptions<AdminAuthOptions> options, ILogger<GrafanaStatus> logger) : IGrafanaStatus
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<GrafanaHealth> CheckAsync(CancellationToken cancellationToken)
    {
        if (HealthUrl(options.Value) is not { } url)
        {
            return new GrafanaHealth(false, "No Grafana URL is configured.");
        }

        try
        {
            using HttpResponseMessage response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new GrafanaHealth(true)
                : new GrafanaHealth(false, $"Its health check answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogCheckFailed(logger, ex.GetType().Name);
            return new GrafanaHealth(false, "It did not answer its health check.");
        }
    }

    internal static Uri? HealthUrl(AdminAuthOptions options) =>
        options.GrafanaHealthUrl is { Length: > 0 } health ? new Uri(health)
        : options.GrafanaUrl is { Length: > 0 } grafana ? new Uri(new Uri(grafana.EndsWith('/') ? grafana : grafana + "/"), "api/health")
        : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Grafana status check failed: {Exception}")]
    private static partial void LogCheckFailed(ILogger logger, string exception);
}
