using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Core;

namespace Nightware.Adapters.Dav.Administration;

/// <summary>
/// Configuration section <c>NextcloudProvisioning</c>: the admin console's own Nextcloud account (ADR 0002 D8),
/// used for OCS user administration and <c>user_oidc</c> pre-provisioning. Never a user's credential.
/// </summary>
public sealed class NextcloudProvisioningOptions
{
    public const string SectionName = "NextcloudProvisioning";

    /// <summary>The <c>user_oidc</c> user backend's name (<c>OCA\UserOIDC\User\Backend::getBackendName()</c> = <c>Application::APP_ID</c>).</summary>
    public const string UserOidcBackend = "user_oidc";

    /// <summary>Nextcloud's base URL, including any sub-path it is hosted under (e.g. <c>https://host.example/cloud/</c>).</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>The console's Nextcloud account (an administrator, Database backend).</summary>
    public string? Username { get; set; }

    /// <summary>That account's password (sent as HTTP Basic; never logged).</summary>
    public string? Password { get; set; }

    /// <summary>
    /// The <c>user_oidc</c> provider users are pre-provisioned for: its numeric id, or its identifier
    /// (e.g. <c>keycloak</c>, as <c>occ user_oidc:provider keycloak</c> named it), which is then looked up
    /// through <c>GET /ocs/v2.php/apps/user_oidc/api/v1/provider</c>.
    /// </summary>
    public string? OidcProviderId { get; set; } = "keycloak";

    /// <summary>
    /// User backends whose accounts sign in through the identity provider. Empty means
    /// <see cref="UserOidcBackend"/> (a list default would be appended to, not replaced, by configuration).
    /// </summary>
    public IList<string> IdentityProviderBackends { get; } = [];

    /// <summary>Time allowed for one HTTP attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Time allowed for a whole request including retries.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Retries for GET only; writes are never replayed.</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary><see cref="IdentityProviderBackends"/>, or <see cref="UserOidcBackend"/> when none is configured.</summary>
    internal IReadOnlyList<string> EffectiveIdentityProviderBackends =>
        IdentityProviderBackends.Where(static b => !string.IsNullOrWhiteSpace(b)).Select(static b => b.Trim()).ToList() is { Count: > 0 } configured
            ? configured
            : [UserOidcBackend];

    /// <summary>The numeric provider id when <see cref="OidcProviderId"/> is one.</summary>
    internal int? NumericOidcProviderId =>
        int.TryParse(OidcProviderId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    /// <summary>The timeouts and retries in the shape <see cref="Core.Http.DavResilience"/> takes.</summary>
    internal DavOptions ToResilienceOptions() => new()
    {
        BaseUrl = BaseUrl,
        AttemptTimeout = AttemptTimeout,
        TotalTimeout = TotalTimeout,
        MaxRetryAttempts = MaxRetryAttempts,
    };
}

/// <summary>Validates <see cref="NextcloudProvisioningOptions"/> at startup.</summary>
internal sealed class NextcloudProvisioningOptionsValidator : IValidateOptions<NextcloudProvisioningOptions>
{
    public ValidateOptionsResult Validate(string? name, NextcloudProvisioningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.BaseUrl is not { } baseUrl
            || !baseUrl.IsAbsoluteUri
            || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUrl.Query)
            || !string.IsNullOrEmpty(baseUrl.Fragment)
            || !string.IsNullOrEmpty(baseUrl.UserInfo))
        {
            failures.Add("NextcloudProvisioning:BaseUrl must be an absolute http(s) URL without credentials, query or fragment.");
        }

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            failures.Add("NextcloudProvisioning:Username is required.");
        }
        else if (options.Username.Contains(':', StringComparison.Ordinal))
        {
            failures.Add("NextcloudProvisioning:Username must not contain ':' (HTTP Basic).");
        }

        if (string.IsNullOrEmpty(options.Password))
        {
            failures.Add("NextcloudProvisioning:Password is required.");
        }

        if (string.IsNullOrWhiteSpace(options.OidcProviderId))
        {
            failures.Add("NextcloudProvisioning:OidcProviderId is required (the user_oidc provider's id or identifier).");
        }

        if (options.AttemptTimeout <= TimeSpan.Zero)
        {
            failures.Add("NextcloudProvisioning:AttemptTimeout must be positive.");
        }

        if (options.TotalTimeout <= TimeSpan.Zero)
        {
            failures.Add("NextcloudProvisioning:TotalTimeout must be positive.");
        }
        else if (options.TotalTimeout < options.AttemptTimeout)
        {
            failures.Add("NextcloudProvisioning:TotalTimeout must be at least NextcloudProvisioning:AttemptTimeout.");
        }

        if (options.MaxRetryAttempts is < 0 or > 10)
        {
            failures.Add("NextcloudProvisioning:MaxRetryAttempts must be between 0 and 10.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
