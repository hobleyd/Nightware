using Microsoft.Extensions.Options;

namespace Nightware.Adapters.Directory.Core;

internal sealed class DirectoryOptionsValidator : IValidateOptions<DirectoryOptions>
{
    public ValidateOptionsResult Validate(string? name, DirectoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];

        if (options.KeycloakBaseUrl is not { IsAbsoluteUri: true } url || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            failures.Add("Directory:KeycloakBaseUrl must be an absolute http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(options.Realm) || options.Realm.Contains('/', StringComparison.Ordinal))
        {
            failures.Add("Directory:Realm is required and must not contain '/'.");
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            failures.Add("Directory:ClientId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            failures.Add("Directory:ClientSecret is required (confidential client).");
        }

        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(5))
        {
            failures.Add("Directory:RequestTimeout must be between 0 and 5 minutes.");
        }

        if (options.MaxRetryAttempts is < 0 or > 10)
        {
            failures.Add("Directory:MaxRetryAttempts must be between 0 and 10.");
        }

        if (options.RetryBaseDelay < TimeSpan.Zero)
        {
            failures.Add("Directory:RetryBaseDelay must not be negative.");
        }

        if (options.ServiceAccountTokenRefreshSkew < TimeSpan.Zero || options.ServiceAccountTokenRefreshSkew > TimeSpan.FromMinutes(10))
        {
            failures.Add("Directory:ServiceAccountTokenRefreshSkew must be between 0 and 10 minutes.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
