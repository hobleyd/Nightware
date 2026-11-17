using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Nightware.Admin.Auth;

/// <summary>Console sign-in and policy settings (configuration section <c>Admin</c>, ADR 0002 D7).</summary>
public sealed class AdminAuthOptions
{
    public const string SectionName = "Admin";

    /// <summary>The Keycloak realm URL, e.g. <c>https://host/auth/realms/nightware</c> (the token <c>iss</c>).</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Where to fetch discovery from when it differs from the public authority (inside the compose network).</summary>
    public string? MetadataAddress { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>The console's own confidential client (<c>nightware-admin</c>), never the Graph façade's.</summary>
    public string ClientId { get; set; } = "nightware-admin";

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary><c>acr</c> values a sign-in must carry. Empty only outside Production (no OTP for dev users).</summary>
    public IList<string> AcceptedAcrValues { get; } = [];

    /// <summary>Sign-out after this long without a request.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Sign-out this long after sign-in, however active.</summary>
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>How recent a sign-in must be to delete a user's data.</summary>
    public TimeSpan DestructiveActionMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Lets entity admins delete data of their own entity's disabled users (ADR 0002 OQ2b).</summary>
    public bool EntityAdminsMayDeleteData { get; set; }

    /// <summary>How long a set-password e-mail link stays valid.</summary>
    public TimeSpan ActionEmailLifespan { get; set; } = TimeSpan.FromDays(3);

    /// <summary>The realm's <c>accessTokenLifespan</c> (keep them equal); shown when offboarding.</summary>
    public TimeSpan AccessTokenLifespan { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long an administrator's scope, resolved from Keycloak, is reused.</summary>
    public TimeSpan ScopeCacheLifetime { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>Refuses to start with an incomplete client, or without MFA in Production.</summary>
internal sealed class AdminAuthOptionsValidator(IHostEnvironment environment) : IValidateOptions<AdminAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminAuthOptions options)
    {
        var failures = new List<string>();
        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out _))
        {
            failures.Add("Admin:Authority must be the absolute Keycloak realm URL.");
        }

        if (options.MetadataAddress is { Length: > 0 } metadata && !Uri.TryCreate(metadata, UriKind.Absolute, out _))
        {
            failures.Add("Admin:MetadataAddress must be an absolute URL.");
        }

        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            failures.Add("Admin:ClientId and Admin:ClientSecret are required.");
        }

        if (options.AcceptedAcrValues.Count == 0 && environment.IsProduction())
        {
            failures.Add("Admin:AcceptedAcrValues must name the multi-factor level (e.g. 'mfa') in Production.");
        }

        if (options.IdleTimeout <= TimeSpan.Zero || options.AbsoluteLifetime < options.IdleTimeout)
        {
            failures.Add("Admin:IdleTimeout must be positive and no longer than Admin:AbsoluteLifetime.");
        }

        if (options.DestructiveActionMaxAge <= TimeSpan.Zero)
        {
            failures.Add("Admin:DestructiveActionMaxAge must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
