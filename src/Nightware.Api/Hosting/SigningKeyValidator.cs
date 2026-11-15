using System.Text;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Nightware.Api.Graph.Json;

namespace Nightware.Api.Hosting;

/// <summary>
/// Refuses to start a Production façade without a real <c>DeltaLinks:SigningKey</c>.
/// </summary>
/// <remarks>
/// The key is the façade's root secret: it signs delta/next links and, through HKDF, keys the
/// AES-GCM seal that carries callers' access tokens inside self-authorising upload and download URLs
/// (ADR 0001 §39). A known key lets anyone forge those links and open sealed URLs, so Production
/// requires one of at least <see cref="MinimumBytes"/> bytes and rejects the development constants.
/// Other environments keep the per-process random fallback (tests, <c>dotnet run</c>).
/// </remarks>
internal sealed class SigningKeyValidator(IHostEnvironment environment) : IValidateOptions<DeltaLinkOptions>
{
    public const int MinimumBytes = 32;

    private const string Setting = "DeltaLinks:SigningKey (environment variable DeltaLinks__SigningKey; "
        + "compose: NIGHTWARE_DELTA_LINK_KEY in deploy/.env)";

    /// <summary>Values that have been published in this repository; never acceptable in Production.</summary>
    private static readonly HashSet<string> PublicKeys = new(StringComparer.Ordinal)
    {
        "dev-only-delta-link-signing-key",
        "development-only-delta-link-signing-key-change-me",
    };

    public ValidateOptionsResult Validate(string? name, DeltaLinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!environment.IsProduction())
        {
            return ValidateOptionsResult.Success;
        }

        var key = options.SigningKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return ValidateOptionsResult.Fail(
                $"{Setting} is required in Production. Generate one with `openssl rand -hex 32` and share it between every instance.");
        }

        if (Encoding.UTF8.GetByteCount(key) < MinimumBytes)
        {
            return ValidateOptionsResult.Fail(
                $"{Setting} must be at least {MinimumBytes} bytes. Generate one with `openssl rand -hex 32`.");
        }

        return PublicKeys.Contains(key)
            ? ValidateOptionsResult.Fail($"{Setting} is a published development value. Generate one with `openssl rand -hex 32`.")
            : ValidateOptionsResult.Success;
    }
}
