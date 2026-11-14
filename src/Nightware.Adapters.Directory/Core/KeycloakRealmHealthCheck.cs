using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Polly;

namespace Nightware.Adapters.Directory.Core;

/// <summary>
/// Healthy when the realm's OIDC discovery document is served with an <c>issuer</c>. The issuer is
/// not compared with <see cref="DirectoryOptions.KeycloakBaseUrl"/>: the façade may reach Keycloak on
/// an internal URL while the issuer is the public <c>KC_HOSTNAME</c>.
/// </summary>
internal sealed class KeycloakRealmHealthCheck(IHttpClientFactory httpClientFactory, IOptions<DirectoryOptions> options) : IHealthCheck
{
    public const string Name = "keycloak";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        HttpClient http = httpClientFactory.CreateClient(KeycloakHttp.TokenClientName);
        try
        {
            using HttpResponseMessage response = await http.GetAsync(options.Value.DiscoveryEndpoint, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Unhealthy($"Keycloak realm discovery returned HTTP {(int)response.StatusCode}.");
            }

            DiscoveryDocument? document = await response.Content.ReadFromJsonAsync<DiscoveryDocument>(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(document?.Issuer)
                ? HealthCheckResult.Unhealthy("Keycloak realm discovery document has no issuer.")
                : HealthCheckResult.Healthy($"Keycloak realm issuer {document.Issuer}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TimeoutException or ExecutionRejectedException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return HealthCheckResult.Unhealthy("Keycloak realm discovery failed.", ex);
        }
    }
}
