namespace Nightware.Adapters.Directory.Core;

/// <summary>
/// Connection to Keycloak (configuration section <c>Directory</c>). The façade authenticates as the
/// confidential <c>nightware-api</c> client: client-credentials for its service account (admin API,
/// ADR 0001 §17) and RFC 8693 standard token exchange on behalf of the caller (§16).
/// </summary>
public sealed class DirectoryOptions
{
    public const string SectionName = "Directory";

    /// <summary>
    /// Keycloak root URL as reachable from the façade, e.g. <c>https://auth.example.com</c>. Not the
    /// realm URL. An internal URL (http://keycloak:8080) is fine; tokens keep the public issuer as
    /// long as Keycloak runs with a fixed <c>KC_HOSTNAME</c>.
    /// </summary>
    public Uri? KeycloakBaseUrl { get; set; }

    public string Realm { get; set; } = string.Empty;

    /// <summary>The confidential client id; <c>nightware-api</c> in the reference deployment.</summary>
    public string ClientId { get; set; } = "nightware-api";

    /// <summary>The client secret. Supply it through a secret store or environment variable, never appsettings.json.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Per-attempt timeout for every Keycloak call.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Retries for idempotent calls (admin GETs, client-credentials). Token exchange is never retried. 0 disables.</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>Base delay of the exponential (jittered) retry back-off.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The service-account token is refreshed this long before it expires.</summary>
    public TimeSpan ServiceAccountTokenRefreshSkew { get; set; } = TimeSpan.FromSeconds(30);

    internal Uri RealmUrl => new($"{BaseUrlWithoutSlash}/realms/{Uri.EscapeDataString(Realm)}");

    internal Uri TokenEndpoint => new($"{RealmUrl}/protocol/openid-connect/token");

    internal Uri DiscoveryEndpoint => new($"{RealmUrl}/.well-known/openid-configuration");

    /// <summary>Admin REST API root for the realm, with a trailing slash so relative paths append.</summary>
    internal Uri AdminRealmUrl => new($"{BaseUrlWithoutSlash}/admin/realms/{Uri.EscapeDataString(Realm)}/");

    private string BaseUrlWithoutSlash =>
        (KeycloakBaseUrl ?? throw new InvalidOperationException("Directory:KeycloakBaseUrl is not configured.")).AbsoluteUri.TrimEnd('/');
}
