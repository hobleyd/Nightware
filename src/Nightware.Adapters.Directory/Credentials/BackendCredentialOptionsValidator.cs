using Microsoft.Extensions.Options;

using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Directory.Credentials;

internal sealed class BackendCredentialOptionsValidator : IValidateOptions<BackendCredentialOptions>
{
    public ValidateOptionsResult Validate(string? name, BackendCredentialOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        ValidatePolicy(nameof(BackendCredentialOptions.Mail), options.Mail, failures);
        ValidatePolicy(nameof(BackendCredentialOptions.Groupware), options.Groupware, failures);

        if (options.Cache is null)
        {
            failures.Add("BackendCredentials:Cache must not be null.");
        }
        else if (options.Cache.ExpirySkew < TimeSpan.Zero || options.Cache.ExpirySkew > TimeSpan.FromMinutes(10))
        {
            failures.Add("BackendCredentials:Cache:ExpirySkew must be between 0 and 10 minutes.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePolicy(string backend, BackendCredentialPolicy? policy, List<string> failures)
    {
        string prefix = $"BackendCredentials:{backend}";
        if (policy is null)
        {
            failures.Add($"{prefix} must not be null.");
            return;
        }

        switch (policy.Strategy)
        {
            case CredentialStrategy.PassThrough:
                break;
            case CredentialStrategy.TokenExchange when string.IsNullOrWhiteSpace(policy.Audience):
                failures.Add($"{prefix}:Audience is required for TokenExchange.");
                break;
            case CredentialStrategy.TokenExchange:
                break;
            case CredentialStrategy.ServiceAccount:
                ValidateServiceAccount(prefix, policy.ServiceAccount, failures);
                break;
            default:
                failures.Add($"{prefix}:Strategy '{policy.Strategy}' is not supported.");
                break;
        }
    }

    private static void ValidateServiceAccount(string prefix, ServiceAccountCredentialOptions? account, List<string> failures)
    {
        if (account is null)
        {
            failures.Add($"{prefix}:ServiceAccount is required for the ServiceAccount strategy.");
            return;
        }

        if (account.Mode == ServiceAccountMode.Basic)
        {
            if (string.IsNullOrWhiteSpace(account.Username) || account.Username.Contains(':', StringComparison.Ordinal))
            {
                failures.Add($"{prefix}:ServiceAccount:Username is required for Basic and must not contain ':'.");
            }

            if (string.IsNullOrEmpty(account.Password))
            {
                failures.Add($"{prefix}:ServiceAccount:Password is required for Basic.");
            }
        }
        else if (account.Mode != ServiceAccountMode.ClientCredentials)
        {
            failures.Add($"{prefix}:ServiceAccount:Mode '{account.Mode}' is not supported.");
        }
    }
}
