using Microsoft.Extensions.Options;

namespace Nightware.Adapters.Jmap.Administration;

/// <summary>
/// Configuration section <c>StalwartManagement</c>: the admin console's own Stalwart principal
/// (ADR 0002 D8). Exactly one of <see cref="Password"/> (Basic with <see cref="Username"/>) or
/// <see cref="ApiKey"/> (<c>Authorization: Bearer</c>) is set. Neither is ever logged.
/// </summary>
public sealed class StalwartManagementOptions
{
    public const string SectionName = "StalwartManagement";

    /// <summary>The JMAP API endpoint the management methods are POSTed to, e.g. <c>http://stalwart:8080/jmap/</c>.</summary>
    public Uri? ApiUrl { get; set; }

    /// <summary>The management principal, e.g. <c>nightware-admin@example.com</c>; required with <see cref="Password"/>.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>A Stalwart API key secret, sent as <c>Authorization: Bearer</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Total time for one management call (all retries included).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Time for one attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Retries for read-only calls (only <c>/get</c> and <c>/query</c>); writes are never retried.</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>
    /// Upper bound on accounts examined when an address is looked up as an alias. Stalwart has no
    /// documented filter by alias, so that lookup pages through accounts; past this bound it fails
    /// instead of claiming "not found".
    /// </summary>
    public int MaxAccountsScanned { get; set; } = 10_000;

    /// <summary>Accounts per page of that scan (keep it within Stalwart's <c>maxObjectsInGet</c>).</summary>
    public int AccountPageSize { get; set; } = 250;

    public override string ToString() => $"StalwartManagement {{ ApiUrl = {ApiUrl}, Username = {Username}, Credential = {(ApiKey is null ? "Password" : "ApiKey")} }}";
}

/// <summary>Validates <see cref="StalwartManagementOptions"/>; registered with <c>ValidateOnStart</c>.</summary>
internal sealed class StalwartManagementOptionsValidator : IValidateOptions<StalwartManagementOptions>
{
    private const string Prefix = StalwartManagementOptions.SectionName + ":";

    public ValidateOptionsResult Validate(string? name, StalwartManagementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.ApiUrl is null)
        {
            failures.Add($"{Prefix}ApiUrl is required.");
        }
        else if (!options.ApiUrl.IsAbsoluteUri || (options.ApiUrl.Scheme != Uri.UriSchemeHttp && options.ApiUrl.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{Prefix}ApiUrl must be an absolute http(s) URL.");
        }

        bool hasPassword = !string.IsNullOrEmpty(options.Password);
        bool hasApiKey = !string.IsNullOrEmpty(options.ApiKey);
        if (hasPassword == hasApiKey)
        {
            failures.Add($"{Prefix}set exactly one of Password or ApiKey.");
        }

        if (hasPassword && string.IsNullOrWhiteSpace(options.Username))
        {
            failures.Add($"{Prefix}Username is required with Password.");
        }
        else if (hasPassword && options.Username!.Contains(':', StringComparison.Ordinal))
        {
            failures.Add($"{Prefix}Username cannot contain ':'.");
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

        if (options.AccountPageSize < 1 || options.MaxAccountsScanned < options.AccountPageSize)
        {
            failures.Add($"{Prefix}AccountPageSize must be at least 1 and no larger than MaxAccountsScanned.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}