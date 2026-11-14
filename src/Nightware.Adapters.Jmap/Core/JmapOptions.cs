using Microsoft.Extensions.Options;

namespace Nightware.Adapters.Jmap.Core;

/// <summary>Configuration section <c>Jmap</c>.</summary>
public sealed class JmapOptions
{
    public const string SectionName = "Jmap";

    /// <summary>
    /// The JMAP Session resource, e.g. <c>http://stalwart:8080/jmap/session</c> or
    /// <c>…/.well-known/jmap</c> (redirects are followed with the credential re-applied).
    /// </summary>
    public Uri? SessionUrl { get; set; }

    /// <summary>
    /// Rewrite the scheme/host/port of the session's <c>apiUrl</c>/<c>uploadUrl</c>/<c>downloadUrl</c>
    /// to <see cref="SessionUrl"/>'s. Stalwart advertises its public URL
    /// (<c>https://mail.example.com/jmap/</c>); the façade talks to it on the internal network.
    /// </summary>
    public bool UseSessionUrlOrigin { get; set; } = true;

    /// <summary>How long a fetched Session is reused for one credential. It is also dropped on 401 and on a <c>sessionState</c> change.</summary>
    public TimeSpan SessionCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Total time for one JMAP API call (all retries included).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Time for one attempt of a JMAP API call.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Total time for a blob upload, or to receive a download's response headers.</summary>
    public TimeSpan BlobTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Retries for safe requests (session fetch, downloads, POSTs made only of /get /query /changes).</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>Used when the session's core capability omits <c>maxObjectsInGet</c>.</summary>
    public int MaxObjectsInGetFallback { get; set; } = 500;

    /// <summary>Used when the session's core capability omits <c>maxCallsInRequest</c>.</summary>
    public int MaxCallsInRequestFallback { get; set; } = 16;

    /// <summary>
    /// Separator for Stalwart master-user (impersonation) logins: <c>&lt;target&gt;&lt;sep&gt;&lt;master&gt;</c>.
    /// Stalwart's documented default is <c>%</c>.
    /// </summary>
    public string MasterUserSeparator { get; set; } = "%";
}

/// <summary>Validates <see cref="JmapOptions"/> when they are first resolved.</summary>
internal sealed class JmapOptionsValidator : IValidateOptions<JmapOptions>
{
    public ValidateOptionsResult Validate(string? name, JmapOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.SessionUrl is null)
        {
            failures.Add("Jmap:SessionUrl is required.");
        }
        else if (!options.SessionUrl.IsAbsoluteUri || (options.SessionUrl.Scheme != Uri.UriSchemeHttp && options.SessionUrl.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("Jmap:SessionUrl must be an absolute http(s) URL.");
        }

        RequirePositive(failures, options.SessionCacheDuration, nameof(options.SessionCacheDuration));
        RequirePositive(failures, options.RequestTimeout, nameof(options.RequestTimeout));
        RequirePositive(failures, options.AttemptTimeout, nameof(options.AttemptTimeout));
        RequirePositive(failures, options.BlobTimeout, nameof(options.BlobTimeout));

        if (options.AttemptTimeout > options.RequestTimeout)
        {
            failures.Add("Jmap:AttemptTimeout cannot exceed Jmap:RequestTimeout.");
        }

        if (options.MaxRetryAttempts is < 0 or > 10)
        {
            failures.Add("Jmap:MaxRetryAttempts must be between 0 and 10.");
        }

        if (options.MaxObjectsInGetFallback < 1)
        {
            failures.Add("Jmap:MaxObjectsInGetFallback must be at least 1.");
        }

        if (options.MaxCallsInRequestFallback < 1)
        {
            failures.Add("Jmap:MaxCallsInRequestFallback must be at least 1.");
        }

        if (string.IsNullOrEmpty(options.MasterUserSeparator) || options.MasterUserSeparator.Contains(':', StringComparison.Ordinal))
        {
            failures.Add("Jmap:MasterUserSeparator must be non-empty and cannot contain ':'.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequirePositive(List<string> failures, TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"Jmap:{name} must be positive.");
        }
    }
}
