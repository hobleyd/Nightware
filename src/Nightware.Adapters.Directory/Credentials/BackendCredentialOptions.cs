using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Directory.Credentials;

/// <summary>
/// How the façade authenticates to each backend (configuration section <c>BackendCredentials</c>).
/// <see cref="Backend.Directory"/> is not configurable: it always uses the <c>nightware-api</c>
/// service account (ADR 0001 §17).
/// </summary>
public sealed class BackendCredentialOptions
{
    public const string SectionName = "BackendCredentials";

    /// <summary>Stalwart (JMAP). Default: token exchange to audience <c>stalwart</c>.</summary>
    public BackendCredentialPolicy Mail { get; set; } = new() { Strategy = CredentialStrategy.TokenExchange, Audience = "stalwart" };

    /// <summary>Nextcloud (DAV/OCS). Default: token exchange to audience <c>nextcloud</c>.</summary>
    public BackendCredentialPolicy Groupware { get; set; } = new() { Strategy = CredentialStrategy.TokenExchange, Audience = "nextcloud" };

    public ExchangedTokenCacheOptions Cache { get; set; } = new();

    internal BackendCredentialPolicy? For(Backend backend) => backend switch
    {
        Backend.Mail => Mail,
        Backend.Groupware => Groupware,
        _ => null,
    };
}

/// <summary>The credential strategy for one backend.</summary>
public sealed class BackendCredentialPolicy
{
    public CredentialStrategy Strategy { get; set; } = CredentialStrategy.TokenExchange;

    /// <summary>For <see cref="CredentialStrategy.TokenExchange"/>: the target Keycloak client id (the backend's <c>aud</c>).</summary>
    public string? Audience { get; set; }

    /// <summary>For <see cref="CredentialStrategy.ServiceAccount"/>.</summary>
    public ServiceAccountCredentialOptions ServiceAccount { get; set; } = new();
}

/// <summary>How a <see cref="CredentialStrategy.ServiceAccount"/> credential is formed.</summary>
public enum ServiceAccountMode
{
    /// <summary>HTTP Basic with <see cref="ServiceAccountCredentialOptions.Username"/>/<see cref="ServiceAccountCredentialOptions.Password"/> (e.g. a Stalwart master user).</summary>
    Basic,

    /// <summary>Bearer token of the <c>nightware-api</c> client-credentials grant.</summary>
    ClientCredentials,
}

/// <summary>
/// Service-account fallback. The credential carries <c>ActAs</c> = the caller's mailbox; the backend
/// adapter decides how to impersonate (for Stalwart, the master-user login format).
/// </summary>
public sealed class ServiceAccountCredentialOptions
{
    public ServiceAccountMode Mode { get; set; } = ServiceAccountMode.Basic;

    public string? Username { get; set; }

    /// <summary>Supply through a secret store or environment variable.</summary>
    public string? Password { get; set; }
}

/// <summary>Cache of exchanged tokens (a disposable <c>IMemoryCache</c>, ADR 0001 §16).</summary>
public sealed class ExchangedTokenCacheOptions
{
    /// <summary>False exchanges on every request (debugging only).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A cached token is dropped this long before it (or the subject token) expires.</summary>
    public TimeSpan ExpirySkew { get; set; } = TimeSpan.FromSeconds(30);
}
