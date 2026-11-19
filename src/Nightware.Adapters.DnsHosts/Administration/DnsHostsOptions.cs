using Microsoft.Extensions.Options;

namespace Nightware.Adapters.DnsHosts.Administration;

/// <summary>
/// Configuration section <c>DnsHosts</c>: how the admin console reaches the DNS hosts other than Cloudflare. Holds no
/// credential: each entity's own (sealed with the entity) is sent per request. Every setting has a working default.
/// </summary>
public sealed class DnsHostsOptions
{
    public const string SectionName = "DnsHosts";

    /// <summary>DigitalOcean's v2 API; overridden only by tests.</summary>
    public Uri DigitalOceanApiBaseUrl { get; set; } = new("https://api.digitalocean.com/v2/");

    /// <summary>Gandi's LiveDNS v5 API.</summary>
    public Uri GandiApiBaseUrl { get; set; } = new("https://api.gandi.net/v5/livedns/");

    /// <summary>Hetzner's Cloud API, which holds Hetzner DNS since its DNS API closed in May 2026.</summary>
    public Uri HetznerApiBaseUrl { get; set; } = new("https://api.hetzner.cloud/v1/");

    /// <summary>Amazon Route 53's API (global: signed for <c>us-east-1</c>).</summary>
    public Uri Route53ApiBaseUrl { get; set; } = new("https://route53.amazonaws.com/2013-04-01/");

    /// <summary>Microsoft Entra ID, where an Azure service principal signs in (<c>{tenant}/oauth2/v2.0/token</c>).</summary>
    public Uri AzureLoginBaseUrl { get; set; } = new("https://login.microsoftonline.com/");

    /// <summary>Azure Resource Manager, which holds Azure DNS zones.</summary>
    public Uri AzureManagementBaseUrl { get; set; } = new("https://management.azure.com/");

    /// <summary>
    /// Google's OAuth token endpoint, where a service account signs in. Always this one, never the <c>token_uri</c>
    /// in a pasted key: a signed assertion goes only to Google.
    /// </summary>
    public Uri GoogleTokenUrl { get; set; } = new("https://oauth2.googleapis.com/token");

    /// <summary>Google Cloud DNS's v1 API.</summary>
    public Uri GoogleDnsBaseUrl { get; set; } = new("https://dns.googleapis.com/dns/v1/");

    /// <summary>Total time for one call (all retries included).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Time for one attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Retries for reads (GET); writes are never retried.</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>Most records read from one zone; a larger zone fails rather than being planned against in part.</summary>
    public int MaxRecords { get; set; } = 10_000;

    internal IEnumerable<(string Name, Uri Url)> BaseUrls =>
    [
        (nameof(DigitalOceanApiBaseUrl), DigitalOceanApiBaseUrl),
        (nameof(GandiApiBaseUrl), GandiApiBaseUrl),
        (nameof(HetznerApiBaseUrl), HetznerApiBaseUrl),
        (nameof(Route53ApiBaseUrl), Route53ApiBaseUrl),
        (nameof(AzureLoginBaseUrl), AzureLoginBaseUrl),
        (nameof(AzureManagementBaseUrl), AzureManagementBaseUrl),
        (nameof(GoogleDnsBaseUrl), GoogleDnsBaseUrl),
    ];
}

/// <summary>Validates <see cref="DnsHostsOptions"/>; registered with <c>ValidateOnStart</c>.</summary>
internal sealed class DnsHostsOptionsValidator : IValidateOptions<DnsHostsOptions>
{
    private const string Prefix = DnsHostsOptions.SectionName + ":";

    public ValidateOptionsResult Validate(string? name, DnsHostsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        foreach ((string setting, Uri url) in options.BaseUrls)
        {
            if (url is not { IsAbsoluteUri: true } || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            {
                failures.Add($"{Prefix}{setting} must be an absolute http(s) URL.");
            }
            else if (!url.AbsolutePath.EndsWith('/'))
            {
                failures.Add($"{Prefix}{setting} must end with '/'.");
            }
        }

        if (options.GoogleTokenUrl is not { IsAbsoluteUri: true } token || (token.Scheme != Uri.UriSchemeHttps && token.Scheme != Uri.UriSchemeHttp))
        {
            failures.Add($"{Prefix}GoogleTokenUrl must be an absolute http(s) URL.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero || options.AttemptTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{Prefix}RequestTimeout and AttemptTimeout must be positive.");
        }
        else if (options.AttemptTimeout > options.RequestTimeout)
        {
            failures.Add($"{Prefix}AttemptTimeout cannot exceed RequestTimeout.");
        }

        if (options.MaxRetryAttempts is < 0 or > 10)
        {
            failures.Add($"{Prefix}MaxRetryAttempts must be between 0 and 10.");
        }

        if (options.MaxRecords < 100)
        {
            failures.Add($"{Prefix}MaxRecords must be at least 100.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
