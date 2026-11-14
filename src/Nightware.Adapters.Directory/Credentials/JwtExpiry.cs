using System.Buffers.Text;
using System.Text.Json;

namespace Nightware.Adapters.Directory.Credentials;

/// <summary>
/// Reads <c>exp</c> from a JWT payload <b>without</b> validating it. Only used to bound how long an
/// exchanged token is cached; the Api's JWT bearer handler has already validated the token.
/// </summary>
internal static class JwtExpiry
{
    /// <summary>The token's <c>exp</c>, or null when it is not a JWT or has none.</summary>
    public static DateTimeOffset? Read(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        string[] segments = token.Split('.');
        if (segments.Length != 3 || segments[1].Length == 0)
        {
            return null;
        }

        try
        {
            byte[] payload = Base64Url.DecodeFromChars(segments[1]);
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("exp", out JsonElement exp)
                && exp.ValueKind == JsonValueKind.Number
                && exp.TryGetInt64(out long seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            // Not a JWT we can read: fall back to the exchanged token's own lifetime.
        }

        return null;
    }
}
