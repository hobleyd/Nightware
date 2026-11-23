using System.Globalization;

using Microsoft.Extensions.Options;

namespace Nightware.Adapters.Dav.Calls;

/// <summary>
/// The <c>Calls</c> section (ADR 0009 D7). Delegated calls are on only when <see cref="SealingKey"/> is set (and Talk is,
/// <c>Talk:PublicBaseUrl</c>); otherwise <c>/communications/calls</c> answers 501.
/// </summary>
public sealed class CallOptions
{
    public const string SectionName = "Calls";

    /// <summary>
    /// Hex, at least 32 bytes: the AES-256-GCM key that seals a call's Talk session (room token, session cookie) into its
    /// id. It lives in the environment and must be the same on every instance; replacing it orphans calls in progress
    /// (they can no longer be read or hung up over Graph, and Talk ends them itself).
    /// </summary>
    public string? SealingKey { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SealingKey);
}

/// <summary>An empty key leaves calls off; a key that is set must be usable, so a typo fails the start rather than every call.</summary>
internal sealed class CallOptionsValidator : IValidateOptions<CallOptions>
{
    public ValidateOptionsResult Validate(string? name, CallOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsConfigured)
        {
            return ValidateOptionsResult.Success;
        }

        var key = options.SealingKey!.Trim();
        return key.Length >= 64 && key.Length % 2 == 0 && key.All(char.IsAsciiHexDigit)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(string.Create(CultureInfo.InvariantCulture, $"Calls:SealingKey must be at least 32 bytes of hex (64 digits; openssl rand -hex 32)."));
    }
}
