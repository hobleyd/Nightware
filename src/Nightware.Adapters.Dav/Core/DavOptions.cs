using Microsoft.Extensions.Options;

namespace Nightware.Adapters.Dav.Core;

/// <summary>Configuration section <c>Dav</c>: where Nextcloud lives and how patient to be with it.</summary>
public sealed class DavOptions
{
    public const string SectionName = "Dav";

    /// <summary>Nextcloud's base URL, including any sub-path it is hosted under (e.g. <c>https://cloud.example.com/nextcloud/</c>).</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Path of the Sabre DAV root below <see cref="BaseUrl"/>.</summary>
    public string DavRoot { get; set; } = "/remote.php/dav";

    /// <summary>Path of the OCS API root below <see cref="BaseUrl"/>.</summary>
    public string OcsRoot { get; set; } = "/ocs/v2.php";

    /// <summary>Time allowed for one HTTP attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Time allowed for a whole request including retries.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Time allowed for a request whose body is streamed from the caller (a drive upload, an upload session's chunk): it
    /// lasts as long as the caller takes to send it, so the attempt and total timeouts don't apply.
    /// </summary>
    public TimeSpan TransferTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Retries for safe methods (GET, HEAD, OPTIONS, PROPFIND, REPORT) only.</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>How long a discovered principal (home sets, outbox) is cached per credential identity.</summary>
    public TimeSpan DiscoveryCacheDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Time allowed for the anonymous health probe.</summary>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>Validates <see cref="DavOptions"/> at startup.</summary>
internal sealed class DavOptionsValidator : IValidateOptions<DavOptions>
{
    public ValidateOptionsResult Validate(string? name, DavOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.BaseUrl is null)
        {
            failures.Add("Dav:BaseUrl is required.");
        }
        else if (!options.BaseUrl.IsAbsoluteUri
                 || (options.BaseUrl.Scheme != Uri.UriSchemeHttps && options.BaseUrl.Scheme != Uri.UriSchemeHttp))
        {
            failures.Add("Dav:BaseUrl must be an absolute http(s) URL.");
        }
        else if (!string.IsNullOrEmpty(options.BaseUrl.Query) || !string.IsNullOrEmpty(options.BaseUrl.Fragment))
        {
            failures.Add("Dav:BaseUrl must not carry a query or fragment.");
        }

        ValidatePath(options.DavRoot, "Dav:DavRoot", failures);
        ValidatePath(options.OcsRoot, "Dav:OcsRoot", failures);
        ValidatePositive(options.AttemptTimeout, "Dav:AttemptTimeout", failures);
        ValidatePositive(options.TotalTimeout, "Dav:TotalTimeout", failures);
        ValidatePositive(options.TransferTimeout, "Dav:TransferTimeout", failures);
        ValidatePositive(options.DiscoveryCacheDuration, "Dav:DiscoveryCacheDuration", failures);
        ValidatePositive(options.HealthCheckTimeout, "Dav:HealthCheckTimeout", failures);
        if (options.TotalTimeout < options.AttemptTimeout)
        {
            failures.Add("Dav:TotalTimeout must be at least Dav:AttemptTimeout.");
        }

        if (options.MaxRetryAttempts is < 0 or > 10)
        {
            failures.Add("Dav:MaxRetryAttempts must be between 0 and 10.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePath(string? path, string key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal)
            || path.Contains('?', StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal))
        {
            failures.Add($"{key} must be an absolute path such as /remote.php/dav.");
        }
    }

    private static void ValidatePositive(TimeSpan value, string key, List<string> failures)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{key} must be positive.");
        }
    }
}
