using Microsoft.Extensions.Options;

namespace Nightware.Adapters.Cloudflare.Administration;

/// <summary>
/// Configuration section <c>Cloudflare</c>: how the admin console reaches Cloudflare's API. Holds no credential:
/// each entity's own API token is kept (sealed) with the entity and sent per request.
/// </summary>
public sealed class CloudflareOptions
{
    public const string SectionName = "Cloudflare";

    /// <summary>Cloudflare's v4 API; overridden only by tests.</summary>
    public Uri ApiBaseUrl { get; set; } = new("https://api.cloudflare.com/client/v4/");

    /// <summary>Total time for one call (all retries included).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Time for one attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Retries for reads (GET); writes are never retried.</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>Records per page when a zone is read.</summary>
    public int RecordPageSize { get; set; } = 500;

    /// <summary>Most records read from one zone; a larger zone fails rather than being planned against in part.</summary>
    public int MaxRecords { get; set; } = 10_000;
}

/// <summary>Validates <see cref="CloudflareOptions"/>; registered with <c>ValidateOnStart</c>.</summary>
internal sealed class CloudflareOptionsValidator : IValidateOptions<CloudflareOptions>
{
    private const string Prefix = CloudflareOptions.SectionName + ":";

    public ValidateOptionsResult Validate(string? name, CloudflareOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.ApiBaseUrl is not { IsAbsoluteUri: true } url || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            failures.Add($"{Prefix}ApiBaseUrl must be an absolute http(s) URL.");
        }
        else if (!url.AbsolutePath.EndsWith('/'))
        {
            failures.Add($"{Prefix}ApiBaseUrl must end with '/'.");
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

        if (options.RecordPageSize is < 5 or > 5000 || options.MaxRecords < options.RecordPageSize)
        {
            failures.Add($"{Prefix}RecordPageSize must be between 5 and 5000 and no larger than MaxRecords.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
